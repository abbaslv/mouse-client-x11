#include "backend_internal.h"
#include <bthledef.h>
#include <stdio.h>
#include <string.h>

#ifndef BLUETOOTH_GATT_FLAG_NONE
#define BLUETOOTH_GATT_FLAG_NONE                   0x00000000u
#endif
#ifndef BLUETOOTH_GATT_FLAG_FORCE_READ_FROM_DEVICE
#define BLUETOOTH_GATT_FLAG_FORCE_READ_FROM_DEVICE 0x00000004u
#endif

#define BLE_POLL_MS 60000

static const GUID X11_GUID_BLUETOOTHLE_DEVICE_INTERFACE = {
    0x781aee18, 0x7733, 0x4ce4, { 0xad, 0xd0, 0x91, 0xf4, 0x1c, 0x67, 0xb5, 0x92 }
};

typedef HRESULT (WINAPI *PFN_BluetoothGATTGetServices)(
    HANDLE hDevice, USHORT ServicesBufferCount,
    PBTH_LE_GATT_SERVICE ServicesBuffer, USHORT* ServicesBufferActual, ULONG Flags);
typedef HRESULT (WINAPI *PFN_BluetoothGATTGetCharacteristics)(
    HANDLE hDevice, PBTH_LE_GATT_SERVICE Service, USHORT CharacteristicsBufferCount,
    PBTH_LE_GATT_CHARACTERISTIC CharacteristicsBuffer, USHORT* CharacteristicsBufferActual, ULONG Flags);
typedef HRESULT (WINAPI *PFN_BluetoothGATTGetCharacteristicValue)(
    HANDLE hDevice, PBTH_LE_GATT_CHARACTERISTIC Characteristic, ULONG CharacteristicValueDataSize,
    PBTH_LE_GATT_CHARACTERISTIC_VALUE CharacteristicValue, USHORT* CharacteristicValueSizeRequired, ULONG Flags);

typedef struct BleGattApi {
    HMODULE                                hDll;
    PFN_BluetoothGATTGetServices           getServices;
    PFN_BluetoothGATTGetCharacteristics    getChars;
    PFN_BluetoothGATTGetCharacteristicValue getCharVal;
} BleGattApi;

static bool Ble_LoadApi(BleGattApi* api) {
    ZeroMemory(api, sizeof(*api));
    api->hDll = LoadLibraryW(L"BluetoothAPIs.dll");
    if (!api->hDll)
        return false;

    api->getServices = (PFN_BluetoothGATTGetServices)(void*)GetProcAddress(api->hDll, "BluetoothGATTGetServices");
    api->getChars    = (PFN_BluetoothGATTGetCharacteristics)(void*)GetProcAddress(api->hDll, "BluetoothGATTGetCharacteristics");
    api->getCharVal  = (PFN_BluetoothGATTGetCharacteristicValue)(void*)GetProcAddress(api->hDll, "BluetoothGATTGetCharacteristicValue");
    if (!api->getServices || !api->getChars || !api->getCharVal) {
        FreeLibrary(api->hDll);
        ZeroMemory(api, sizeof(*api));
        return false;
    }
    return true;
}

static void Ble_UnloadApi(BleGattApi* api) {
    if (api->hDll) {
        FreeLibrary(api->hDll);
        api->hDll = NULL;
    }
}

static bool Ble_IsShortUuid(const BTH_LE_UUID* u, USHORT expected) {
    if (u->IsShortUuid)
        return u->Value.ShortUuid == expected;
    GUID base = { 0x00000000, 0x0000, 0x1000, { 0x80, 0x00, 0x00, 0x80, 0x5f, 0x9b, 0x34, 0xfb } };
    base.Data1 = expected;
    return memcmp(&u->Value.LongUuid, &base, sizeof(GUID)) == 0;
}

static bool Ble_ReadCharBytes(const BleGattApi* api, HANDLE hDev, PBTH_LE_GATT_CHARACTERISTIC ch, ULONG flags, uint8_t* outBuf, USHORT bufCap, USHORT* outLen) {
    USHORT reqSize = 0;
    HRESULT hr = api->getCharVal(hDev, ch, 0, NULL, &reqSize, flags);
    if (hr != HRESULT_FROM_WIN32(ERROR_MORE_DATA) && FAILED(hr))
        return false;
    if (reqSize == 0 || reqSize > 512)
        return false;

    BYTE mem[512];
    ZeroMemory(mem, reqSize);
    PBTH_LE_GATT_CHARACTERISTIC_VALUE val = (PBTH_LE_GATT_CHARACTERISTIC_VALUE)mem;
    hr = api->getCharVal(hDev, ch, reqSize, val, NULL, flags);
    if (FAILED(hr) || val->DataSize == 0)
        return false;

    USHORT copyLen = (val->DataSize < bufCap) ? (USHORT)val->DataSize : bufCap;
    memcpy(outBuf, val->Data, copyLen);
    *outLen = copyLen;
    return true;
}

static HANDLE Ble_OpenHandle(const wchar_t* path) {
    HANDLE h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE)
        h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    return h;
}

static bool Ble_InspectDevice(
    const BleGattApi* api, HANDLE hDev,
    bool* outHasBat, bool* outHasHid, USHORT* outAppearance,
    wchar_t* outName, size_t nameChars,
    BTH_LE_GATT_CHARACTERISTIC* outBatChar)
{
    *outHasBat = false;
    *outHasHid = false;
    *outAppearance = 0;
    if (outName && nameChars > 0) outName[0] = L'\0';

    USHORT svcCount = 0;
    HRESULT hr = api->getServices(hDev, 0, NULL, &svcCount, BLUETOOTH_GATT_FLAG_NONE);
    if ((hr != HRESULT_FROM_WIN32(ERROR_MORE_DATA) && FAILED(hr)) || svcCount == 0)
        return false;
    if (svcCount > 32) svcCount = 32;

    BTH_LE_GATT_SERVICE svcs[32];
    if (FAILED(api->getServices(hDev, svcCount, svcs, &svcCount, BLUETOOTH_GATT_FLAG_NONE)))
        return false;

    BTH_LE_GATT_SERVICE* batSvc = NULL;
    BTH_LE_GATT_SERVICE* gapSvc = NULL;
    for (USHORT i = 0; i < svcCount; i++) {
        if (Ble_IsShortUuid(&svcs[i].ServiceUuid, 0x180F)) batSvc = &svcs[i];
        else if (Ble_IsShortUuid(&svcs[i].ServiceUuid, 0x1812)) *outHasHid = true;
        else if (Ble_IsShortUuid(&svcs[i].ServiceUuid, 0x1800)) gapSvc = &svcs[i];
    }

    if (batSvc) {
        USHORT chCount = 0;
        hr = api->getChars(hDev, batSvc, 0, NULL, &chCount, BLUETOOTH_GATT_FLAG_NONE);
        if ((hr == HRESULT_FROM_WIN32(ERROR_MORE_DATA) || SUCCEEDED(hr)) && chCount > 0) {
            if (chCount > 16) chCount = 16;
            BTH_LE_GATT_CHARACTERISTIC chars[16];
            if (SUCCEEDED(api->getChars(hDev, batSvc, chCount, chars, &chCount, BLUETOOTH_GATT_FLAG_NONE))) {
                for (USHORT i = 0; i < chCount; i++) {
                    if (Ble_IsShortUuid(&chars[i].CharacteristicUuid, 0x2A19) && chars[i].IsReadable) {
                        *outBatChar = chars[i];
                        *outHasBat = true;
                        break;
                    }
                }
            }
        }
    }

    if (gapSvc) {
        USHORT chCount = 0;
        hr = api->getChars(hDev, gapSvc, 0, NULL, &chCount, BLUETOOTH_GATT_FLAG_NONE);
        if ((hr == HRESULT_FROM_WIN32(ERROR_MORE_DATA) || SUCCEEDED(hr)) && chCount > 0) {
            if (chCount > 16) chCount = 16;
            BTH_LE_GATT_CHARACTERISTIC chars[16];
            if (SUCCEEDED(api->getChars(hDev, gapSvc, chCount, chars, &chCount, BLUETOOTH_GATT_FLAG_NONE))) {
                for (USHORT i = 0; i < chCount; i++) {
                    uint8_t raw[64];
                    USHORT rlen = 0;
                    if (Ble_IsShortUuid(&chars[i].CharacteristicUuid, 0x2A01) && chars[i].IsReadable) {
                        if (Ble_ReadCharBytes(api, hDev, &chars[i], BLUETOOTH_GATT_FLAG_NONE, raw, sizeof(raw), &rlen) && rlen >= 2) {
                            *outAppearance = (USHORT)(raw[0] | ((USHORT)raw[1] << 8));
                        }
                    } else if (outName && nameChars > 0 && Ble_IsShortUuid(&chars[i].CharacteristicUuid, 0x2A00) && chars[i].IsReadable) {
                        if (Ble_ReadCharBytes(api, hDev, &chars[i], BLUETOOTH_GATT_FLAG_NONE, raw, 63, &rlen) && rlen > 0) {
                            raw[rlen] = '\0';
                            MultiByteToWideChar(CP_UTF8, 0, (const char*)raw, -1, outName, (int)nameChars);
                            outName[nameChars - 1] = L'\0';
                        }
                    }
                }
            }
        }
    }

    return *outHasBat && *outHasHid;
}

static bool Ble_IsMouseAppearanceOrName(USHORT appearance, const wchar_t* name) {
    /* 0x03C2 = Standard BLE Mouse; (0x03C0 & 0xFFC0) with subcat 2 = HID Mouse */
    if (appearance != 0) {
        if (appearance == 0x03C2)
            return true;
        if ((appearance & 0xFFC0u) == 0x03C0u && (appearance & 0x003Fu) != 0x0002u)
            return false; /* Keyboard/Joystick/Gamepad etc. */
    }
    if (name && name[0]) {
        if (Backend_StrContainsI(name, L"Keyboard") ||
            Backend_StrContainsI(name, L"Headset") ||
            Backend_StrContainsI(name, L"Gamepad") ||
            Backend_StrContainsI(name, L"Watch"))
            return false;
    }
    return true;
}

bool BleBackend_FindMouse(DetectedDevice* outDev) {
    BleGattApi api;
    if (!Ble_LoadApi(&api))
        return false;

    HDEVINFO devInfo = SetupDiGetClassDevsW(&X11_GUID_BLUETOOTHLE_DEVICE_INTERFACE, NULL, NULL, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
    if (devInfo == INVALID_HANDLE_VALUE) {
        Ble_UnloadApi(&api);
        return false;
    }

    bool found = false;
    BYTE detailMem[sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) + 520 * sizeof(wchar_t)];
    for (DWORD idx = 0;; idx++) {
        SP_DEVICE_INTERFACE_DATA ifData;
        ZeroMemory(&ifData, sizeof(ifData));
        ifData.cbSize = sizeof(ifData);
        if (!SetupDiEnumDeviceInterfaces(devInfo, NULL, &X11_GUID_BLUETOOTHLE_DEVICE_INTERFACE, idx, &ifData))
            break;

        PSP_DEVICE_INTERFACE_DETAIL_DATA_W detail = (PSP_DEVICE_INTERFACE_DETAIL_DATA_W)detailMem;
        ZeroMemory(detailMem, sizeof(detailMem));
        detail->cbSize = sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W);
        if (!SetupDiGetDeviceInterfaceDetailW(devInfo, &ifData, detail, sizeof(detailMem), NULL, NULL))
            continue;

        HANDLE hDev = Ble_OpenHandle(detail->DevicePath);
        if (hDev == INVALID_HANDLE_VALUE)
            continue;

        bool hasBat = false, hasHid = false;
        USHORT appearance = 0;
        wchar_t name[64] = {0};
        BTH_LE_GATT_CHARACTERISTIC batChar;
        bool ok = Ble_InspectDevice(&api, hDev, &hasBat, &hasHid, &appearance, name, 64, &batChar);
        CloseHandle(hDev);

        if (ok && Ble_IsMouseAppearanceOrName(appearance, name)) {
            ZeroMemory(outDev, sizeof(*outDev));
            outDev->backend_id = BACKEND_BLE_GATT;
            outDev->mode = CONN_BLUETOOTH;
            outDev->priority = (appearance == 0x03C2 || Backend_StrContainsI(name, L"Mouse")) ? 35 : 30;
            wcsncpy(outDev->path, detail->DevicePath, 511);
            outDev->path[511] = L'\0';
            if (name[0]) {
                wcsncpy(outDev->product_name, name, 63);
                outDev->product_name[63] = L'\0';
            } else {
                wcscpy(outDev->product_name, L"Bluetooth Mouse");
            }
            found = true;
            break;
        }
    }

    SetupDiDestroyDeviceInfoList(devInfo);
    Ble_UnloadApi(&api);
    return found;
}

void BleBackend_ScanAll(void) {
    BleGattApi api;
    if (!Ble_LoadApi(&api)) {
        wprintf(L"BLE GATT: BluetoothAPIs.dll unavailable.\n");
        return;
    }

    HDEVINFO devInfo = SetupDiGetClassDevsW(&X11_GUID_BLUETOOTHLE_DEVICE_INTERFACE, NULL, NULL, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
    if (devInfo == INVALID_HANDLE_VALUE) {
        Ble_UnloadApi(&api);
        return;
    }

    int count = 0;
    BYTE detailMem[sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) + 520 * sizeof(wchar_t)];
    for (DWORD idx = 0;; idx++) {
        SP_DEVICE_INTERFACE_DATA ifData;
        ZeroMemory(&ifData, sizeof(ifData));
        ifData.cbSize = sizeof(ifData);
        if (!SetupDiEnumDeviceInterfaces(devInfo, NULL, &X11_GUID_BLUETOOTHLE_DEVICE_INTERFACE, idx, &ifData))
            break;

        PSP_DEVICE_INTERFACE_DETAIL_DATA_W detail = (PSP_DEVICE_INTERFACE_DETAIL_DATA_W)detailMem;
        ZeroMemory(detailMem, sizeof(detailMem));
        detail->cbSize = sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W);
        if (!SetupDiGetDeviceInterfaceDetailW(devInfo, &ifData, detail, sizeof(detailMem), NULL, NULL))
            continue;

        HANDLE hDev = Ble_OpenHandle(detail->DevicePath);
        if (hDev == INVALID_HANDLE_VALUE)
            continue;

        bool hasBat = false, hasHid = false;
        USHORT appearance = 0;
        wchar_t name[64] = {0};
        BTH_LE_GATT_CHARACTERISTIC batChar;
        Ble_InspectDevice(&api, hDev, &hasBat, &hasHid, &appearance, name, 64, &batChar);

        uint8_t batBytes[4] = {0};
        USHORT rlen = 0;
        int pct = -1;
        if (hasBat && Ble_ReadCharBytes(&api, hDev, &batChar, BLUETOOTH_GATT_FLAG_NONE, batBytes, sizeof(batBytes), &rlen) && rlen >= 1)
            pct = (batBytes[0] <= 100) ? (int)batBytes[0] : -1;

        CloseHandle(hDev);
        count++;
        wprintf(L"  BLE #%d: name=\"%ls\" hid=%d bat=%d appearance=0x%04X level=%d%%\n",
                count, name[0] ? name : L"(unnamed)", hasHid ? 1 : 0, hasBat ? 1 : 0, appearance, pct);
    }

    if (count == 0)
        wprintf(L"  (no present BLE GATT devices)\n");

    SetupDiDestroyDeviceInfoList(devInfo);
    Ble_UnloadApi(&api);
}

static bool Ble_ReadBatteryPercent(const BleGattApi* api, HANDLE hDev, BTH_LE_GATT_CHARACTERISTIC* batChar, DeviceStatus* st) {
    uint8_t raw[8] = {0};
    USHORT rlen = 0;
    if (!Ble_ReadCharBytes(api, hDev, batChar, BLUETOOTH_GATT_FLAG_FORCE_READ_FROM_DEVICE, raw, sizeof(raw), &rlen)) {
        if (!Ble_ReadCharBytes(api, hDev, batChar, BLUETOOTH_GATT_FLAG_NONE, raw, sizeof(raw), &rlen))
            return false;
    }
    if (rlen < 1 || raw[0] > 100)
        return false;

    st->battery_pct = (int)raw[0];
    st->is_charging = false;
    st->is_full = (st->battery_pct >= 100);
    st->has_reading = true;
    st->is_awake = true;
    st->last_update_tick64 = GetTickCount64();
    return true;
}

static DWORD WINAPI Ble_RunThread(LPVOID param) {
    BackendRuntime* rt = (BackendRuntime*)param;
    DeviceStatus st;
    Backend_InitDeviceStatus(&st, &rt->dev, g_backendBle.label);

    BleGattApi api;
    if (!Ble_LoadApi(&api)) {
        rt->notify_disconnected();
        return 0;
    }

    HANDLE hDev = Ble_OpenHandle(rt->dev.path);
    if (hDev == INVALID_HANDLE_VALUE) {
        Ble_UnloadApi(&api);
        rt->notify_disconnected();
        return 0;
    }

    bool hasBat = false, hasHid = false;
    USHORT appearance = 0;
    BTH_LE_GATT_CHARACTERISTIC batChar;
    if (!Ble_InspectDevice(&api, hDev, &hasBat, &hasHid, &appearance, NULL, 0, &batChar)) {
        CloseHandle(hDev);
        Ble_UnloadApi(&api);
        rt->notify_disconnected();
        return 0;
    }

    HANDLE waits[2] = { rt->stop_event, rt->poll_now_event };
    bool disconnected = false;

    for (;;) {
        if (Ble_ReadBatteryPercent(&api, hDev, &batChar, &st)) {
            rt->publish(&st);
        } else {
            DWORD err = GetLastError();
            if (err == ERROR_DEVICE_NOT_CONNECTED || err == ERROR_INVALID_HANDLE) {
                disconnected = true;
                break;
            }
        }
        DWORD w = WaitForMultipleObjects(2, waits, FALSE, BLE_POLL_MS);
        if (w == WAIT_OBJECT_0)
            break;
    }

    CloseHandle(hDev);
    Ble_UnloadApi(&api);
    if (disconnected)
        rt->notify_disconnected();
    return 0;
}

static bool Ble_ProbeOnce(const DetectedDevice* dev, int timeoutMs, DeviceStatus* outStatus) {
    (void)timeoutMs;
    Backend_InitDeviceStatus(outStatus, dev, g_backendBle.label);

    BleGattApi api;
    if (!Ble_LoadApi(&api))
        return true;

    HANDLE hDev = Ble_OpenHandle(dev->path);
    if (hDev != INVALID_HANDLE_VALUE) {
        bool hasBat = false, hasHid = false;
        USHORT appearance = 0;
        BTH_LE_GATT_CHARACTERISTIC batChar;
        if (Ble_InspectDevice(&api, hDev, &hasBat, &hasHid, &appearance, NULL, 0, &batChar))
            Ble_ReadBatteryPercent(&api, hDev, &batChar, outStatus);
        CloseHandle(hDev);
    }
    Ble_UnloadApi(&api);
    return true;
}

const MouseBackendOps g_backendBle = {
    BACKEND_BLE_GATT,
    L"BLE GATT",
    NULL,
    Ble_RunThread,
    Ble_ProbeOnce
};
