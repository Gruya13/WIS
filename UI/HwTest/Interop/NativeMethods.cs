using System;
using System.Runtime.InteropServices;

namespace WIS.HwTest.Interop
{
    public static class NativeMethods
    {
        #region Constants
        public const int WH_KEYBOARD_LL = 13;
        public const int WH_MOUSE_LL = 14;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;
        public const int WM_MBUTTONDOWN = 0x0207;
        public const int WM_MBUTTONUP = 0x0208;
        public const int WM_MOUSEHWHEEL = 0x020E;
        public const int VK_MBUTTON = 0x04;
        public const int WM_DEVICECHANGE = 0x0219;
        public const int DBT_DEVICEARRIVAL = 0x8000;
        public const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        public const int WM_POWERBROADCAST = 0x0218;
        public const int WM_SYSCOMMAND = 0x0112;
        public const int WM_APPCOMMAND = 0x0319;
        public const int SC_KEYMENU = 0xF100;
        public const int GWL_STYLE = -16;
        public const int WS_SYSMENU = 0x00080000;

        public const int SM_MAXIMUMTOUCHES = 95;
        public const int SM_DIGITIZER = 94;

        public const int WM_INPUT = 0x00FF;
        public const uint RIDEV_REMOVE = 0x00000001;
        public const uint RIDEV_INPUTSINK = 0x00000100;
        public const uint RID_INPUT = 0x10000003;
        public const int RIM_TYPEMOUSE = 0;
        public const int RIM_TYPEKEYBOARD = 1;
        public const int RIM_TYPEHID = 2;
        public const uint RIDI_DEVICENAME = 0x20000007u;
        public const uint RIDI_DEVICEINFO = 0x2000000Bu;
        public const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001;
        public const ushort RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004;
        public const ushort RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010;
        public const ushort RI_MOUSE_MIDDLE_BUTTON_UP = 0x0020;
        public const ushort RI_MOUSE_WHEEL = 0x0400;

        public const uint EVENT_MODIFY_STATE = 0x0002;
        public const uint SYNCHRONIZE = 0x00100000;
        public const uint WAIT_OBJECT_0 = 0x00000000;
        public const uint INFINITE = 0xFFFFFFFFu;

        public const uint SPI_GETTOUCHPADPARAMETERS = 0x00AEu;
        public const uint SPI_SETTOUCHPADPARAMETERS = 0x00AFu;
        public const uint SPIF_SENDCHANGE = 0x0002u;
        public const uint TOUCHPAD_PARAMETERS_VERSION_1 = 1u;
        public const uint TOUCHPAD_FLAG1_PRESENT = 0x00000001u;
        public const uint TOUCHPAD_FLAG1_LEGACY_PRESENT = 0x00000002u;
        public const uint TOUCHPAD_FLAG1_EXTERNAL_MOUSE_PRESENT = 0x00000004u;
        public const uint TOUCHPAD_FLAG1_ENABLED = 0x00000008u;
        public const uint TOUCHPAD_FLAG1_ACTIVE = 0x00000010u;
        #endregion

        #region Delegates
        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        #endregion

        #region Structs
        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICELIST
        {
            public IntPtr hDevice;
            public uint dwType;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;        // 0 = Offline, 1 = Online, 255 = Unknown
            public byte BatteryFlag;         // 128 = No system battery
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WAVEINCAPSW
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TOUCHPAD_PARAMETERS_V1
        {
            public uint versionNumber;
            public uint maxSupportedContacts;
            public uint legacyTouchpadFeatures;
            public uint flags1;
            public uint flags2;
            public uint sensitivityLevel;
            public uint cursorSpeed;
            public uint feedbackIntensity;
            public uint clickForceSensitivity;
            public uint rightClickZoneWidth;
            public uint rightClickZoneHeight;
        }
        #endregion

        #region User32
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern int GetKeyboardType(int nTypeFlag);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static void EnableDarkTitleBar(System.Windows.Window win)
        {
            if (win == null) return;
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(win);
                IntPtr hwnd = helper.Handle;
                if (hwnd == IntPtr.Zero) return;

                int darkMode = 1;
                DwmSetWindowAttribute(hwnd, 20, ref darkMode, sizeof(int));
                DwmSetWindowAttribute(hwnd, 19, ref darkMode, sizeof(int));

                // BGR format za #202228 (R=0x20, G=0x22, B=0x28 -> 0x00282220)
                int captionColor = 0x00282220;
                DwmSetWindowAttribute(hwnd, 35, ref captionColor, sizeof(int));

                // BGR format za #323640 (R=0x32, G=0x36, B=0x40 -> 0x00403632)
                int borderColor = 0x00403632;
                DwmSetWindowAttribute(hwnd, 34, ref borderColor, sizeof(int));

                int textColor = 0x00FFFFFF;
                DwmSetWindowAttribute(hwnd, 36, ref textColor, sizeof(int));
            }
            catch { }
        }

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterRawInputDevices(
            RAWINPUTDEVICE[] pRawInputDevices,
            uint uiNumDevices,
            uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetRawInputData(
            IntPtr hRawInput,
            uint uiCommand,
            IntPtr pData,
            ref uint pcbSize,
            uint cbSizeHeader);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenEvent(
            uint dwDesiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
            string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetEvent(IntPtr hEvent);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForMultipleObjects(uint nCount, IntPtr[] lpHandles, [MarshalAs(UnmanagedType.Bool)] bool bWaitAll, uint dwMilliseconds);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetRawInputDeviceList(
            IntPtr pRawInputDeviceList,
            ref uint puiNumDevices,
            uint cbSize);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern uint GetRawInputDeviceInfo(
            IntPtr hDevice,
            uint uiCommand,
            IntPtr pData,
            ref uint pcbSize);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SystemParametersInfo(
            uint uiAction,
            uint uiParam,
            ref TOUCHPAD_PARAMETERS_V1 pvParam,
            uint fWinIni);

        public static bool TryGetPrecisionTouchpadEnabled(out bool isEnabled)
        {
            isEnabled = false;
            try
            {
                TOUCHPAD_PARAMETERS_V1 tp = new TOUCHPAD_PARAMETERS_V1();
                tp.versionNumber = TOUCHPAD_PARAMETERS_VERSION_1;
                uint cbSize = (uint)Marshal.SizeOf(typeof(TOUCHPAD_PARAMETERS_V1));
                if (!SystemParametersInfo(SPI_GETTOUCHPADPARAMETERS, cbSize, ref tp, 0u))
                {
                    return false;
                }
                if ((tp.flags1 & TOUCHPAD_FLAG1_PRESENT) == 0u)
                {
                    return false;
                }
                isEnabled = (tp.flags1 & TOUCHPAD_FLAG1_ENABLED) != 0u;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool TrySetPrecisionTouchpadEnabled(bool enabled, out bool previousEnabled)
        {
            previousEnabled = false;
            try
            {
                TOUCHPAD_PARAMETERS_V1 tp = new TOUCHPAD_PARAMETERS_V1();
                tp.versionNumber = TOUCHPAD_PARAMETERS_VERSION_1;
                uint cbSize = (uint)Marshal.SizeOf(typeof(TOUCHPAD_PARAMETERS_V1));
                if (!SystemParametersInfo(SPI_GETTOUCHPADPARAMETERS, cbSize, ref tp, 0u))
                {
                    return false;
                }
                if ((tp.flags1 & TOUCHPAD_FLAG1_PRESENT) == 0u)
                {
                    return false;
                }

                previousEnabled = (tp.flags1 & TOUCHPAD_FLAG1_ENABLED) != 0u;
                if (enabled == previousEnabled)
                {
                    return true;
                }

                if (enabled)
                    tp.flags1 |= TOUCHPAD_FLAG1_ENABLED;
                else
                    tp.flags1 &= ~TOUCHPAD_FLAG1_ENABLED;

                return SystemParametersInfo(SPI_SETTOUCHPADPARAMETERS, cbSize, ref tp, SPIF_SENDCHANGE);
            }
            catch
            {
                return false;
            }
        }

        public sealed class RawInputDeviceMeta
        {
            public IntPtr Handle;
            public uint RawType;
            public string DeviceName;
            public uint VendorId;
            public uint ProductId;
            public ushort UsagePage;
            public ushort Usage;
        }

        public static bool TryGetRawInputDeviceInfoDetails(
            IntPtr hDevice,
            out uint rawType,
            out uint vendorId,
            out uint productId,
            out ushort usagePage,
            out ushort usage)
        {
            rawType = 0u;
            vendorId = 0u;
            productId = 0u;
            usagePage = 0;
            usage = 0;
            if (hDevice == IntPtr.Zero) return false;
            IntPtr infoPtr = IntPtr.Zero;
            try
            {
                uint cbSize = 32u;
                infoPtr = Marshal.AllocHGlobal(32);
                Marshal.WriteInt32(infoPtr, 0, 32);
                if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICEINFO, infoPtr, ref cbSize) == unchecked((uint)-1) || cbSize < 8u)
                {
                    return false;
                }
                rawType = unchecked((uint)Marshal.ReadInt32(infoPtr, 4));
                if (rawType == RIM_TYPEHID && cbSize >= 24u)
                {
                    vendorId = unchecked((uint)Marshal.ReadInt32(infoPtr, 8));
                    productId = unchecked((uint)Marshal.ReadInt32(infoPtr, 12));
                    usagePage = unchecked((ushort)Marshal.ReadInt16(infoPtr, 20));
                    usage = unchecked((ushort)Marshal.ReadInt16(infoPtr, 22));
                }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (infoPtr != IntPtr.Zero)
                {
                    try { Marshal.FreeHGlobal(infoPtr); } catch { }
                }
            }
        }

        public static System.Collections.Generic.List<RawInputDeviceMeta> EnumRawInputDevicesMeta()
        {
            var result = new System.Collections.Generic.List<RawInputDeviceMeta>();
            IntPtr listPtr = IntPtr.Zero;
            try
            {
                uint numDevices = 0;
                uint itemSize = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
                if (GetRawInputDeviceList(IntPtr.Zero, ref numDevices, itemSize) != 0 || numDevices == 0 || numDevices > 512)
                {
                    return result;
                }
                listPtr = Marshal.AllocHGlobal((int)(numDevices * itemSize));
                uint got = GetRawInputDeviceList(listPtr, ref numDevices, itemSize);
                if (got == unchecked((uint)-1)) return result;

                for (uint i = 0; i < got; i++)
                {
                    IntPtr entryPtr = new IntPtr(listPtr.ToInt64() + (long)i * itemSize);
                    RAWINPUTDEVICELIST entry = (RAWINPUTDEVICELIST)Marshal.PtrToStructure(entryPtr, typeof(RAWINPUTDEVICELIST));
                    uint rawType, vid, pid;
                    ushort uPage, uId;
                    TryGetRawInputDeviceInfoDetails(entry.hDevice, out rawType, out vid, out pid, out uPage, out uId);
                    result.Add(new RawInputDeviceMeta
                    {
                        Handle = entry.hDevice,
                        RawType = entry.dwType,
                        DeviceName = GetRawInputDeviceNameString(entry.hDevice),
                        VendorId = vid,
                        ProductId = pid,
                        UsagePage = uPage,
                        Usage = uId
                    });
                }
            }
            catch { }
            finally
            {
                if (listPtr != IntPtr.Zero) { try { Marshal.FreeHGlobal(listPtr); } catch { } }
            }
            return result;
        }

        public static string GetRawInputDeviceNameString(IntPtr hDevice)
        {
            if (hDevice == IntPtr.Zero) return "";
            IntPtr buf = IntPtr.Zero;
            try
            {
                uint charCount = 0;
                GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref charCount);
                if (charCount == 0 || charCount > 2048) return "";
                buf = Marshal.AllocHGlobal((int)(charCount * 2));
                uint read = GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, buf, ref charCount);
                if (read == unchecked((uint)-1)) return "";
                return Marshal.PtrToStringUni(buf) ?? "";
            }
            catch
            {
                return "";
            }
            finally
            {
                if (buf != IntPtr.Zero)
                {
                    try { Marshal.FreeHGlobal(buf); } catch { }
                }
            }
        }

        public static System.Collections.Generic.List<uint> EnumRawHidUsagePairs()
        {
            var result = new System.Collections.Generic.List<uint>();
            var seen = new System.Collections.Generic.HashSet<uint>();
            IntPtr listPtr = IntPtr.Zero;
            IntPtr infoPtr = IntPtr.Zero;
            try
            {
                uint numDevices = 0;
                uint itemSize = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
                if (GetRawInputDeviceList(IntPtr.Zero, ref numDevices, itemSize) != 0 || numDevices == 0 || numDevices > 512)
                {
                    return result;
                }
                listPtr = Marshal.AllocHGlobal((int)(numDevices * itemSize));
                uint got = GetRawInputDeviceList(listPtr, ref numDevices, itemSize);
                if (got == unchecked((uint)-1)) return result;

                infoPtr = Marshal.AllocHGlobal(32);
                for (uint i = 0; i < got; i++)
                {
                    IntPtr entryPtr = new IntPtr(listPtr.ToInt64() + (long)i * itemSize);
                    RAWINPUTDEVICELIST entry = (RAWINPUTDEVICELIST)Marshal.PtrToStructure(entryPtr, typeof(RAWINPUTDEVICELIST));
                    if (entry.dwType != RIM_TYPEHID) continue;

                    uint cbSize = 32;
                    Marshal.WriteInt32(infoPtr, 0, 32);
                    if (GetRawInputDeviceInfo(entry.hDevice, RIDI_DEVICEINFO, infoPtr, ref cbSize) != unchecked((uint)-1) && cbSize >= 24)
                    {
                        ushort usagePage = unchecked((ushort)Marshal.ReadInt16(infoPtr, 20));
                        ushort usage = unchecked((ushort)Marshal.ReadInt16(infoPtr, 22));
                        uint packed = ((uint)usagePage << 16) | (uint)usage;
                        if (!seen.Contains(packed))
                        {
                            seen.Add(packed);
                            result.Add(packed);
                        }
                    }
                }
            }
            catch { }
            finally
            {
                if (infoPtr != IntPtr.Zero) { try { Marshal.FreeHGlobal(infoPtr); } catch { } }
                if (listPtr != IntPtr.Zero) { try { Marshal.FreeHGlobal(listPtr); } catch { } }
            }
            return result;
        }

        public static bool TryReadRawInputPacket(
            IntPtr lParam,
            out uint rawType,
            out IntPtr rawDevice,
            out ushort buttonFlags,
            out short buttonData,
            out int lastX,
            out int lastY,
            out byte[] hidSample)
        {
            rawType = 0;
            rawDevice = IntPtr.Zero;
            buttonFlags = 0;
            buttonData = 0;
            lastX = 0;
            lastY = 0;
            hidSample = null;
            IntPtr buf = IntPtr.Zero;
            try
            {
                uint headerSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
                uint dwSize = 0;
                if (GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref dwSize, headerSize) != 0 || dwSize < headerSize || dwSize > 4096)
                {
                    return false;
                }
                buf = Marshal.AllocHGlobal((int)dwSize);
                if (GetRawInputData(lParam, RID_INPUT, buf, ref dwSize, headerSize) != dwSize)
                {
                    return false;
                }
                RAWINPUTHEADER hdr = (RAWINPUTHEADER)Marshal.PtrToStructure(buf, typeof(RAWINPUTHEADER));
                rawType = hdr.dwType;
                rawDevice = hdr.hDevice;
                if (hdr.dwType == RIM_TYPEMOUSE && dwSize >= headerSize + 20)
                {
                    buttonFlags = unchecked((ushort)Marshal.ReadInt16(buf, (int)headerSize + 4));
                    buttonData = Marshal.ReadInt16(buf, (int)headerSize + 6);
                    lastX = Marshal.ReadInt32(buf, (int)headerSize + 12);
                    lastY = Marshal.ReadInt32(buf, (int)headerSize + 16);
                }
                else if (hdr.dwType == RIM_TYPEHID && dwSize >= headerSize + 8)
                {
                    uint sizeHid = unchecked((uint)Marshal.ReadInt32(buf, (int)headerSize));
                    uint count = unchecked((uint)Marshal.ReadInt32(buf, (int)headerSize + 4));
                    uint payloadLen = dwSize - headerSize - 8;
                    if (sizeHid > 0 && count > 0 && payloadLen > 0)
                    {
                        int copyLen = (int)Math.Min(payloadLen, 32u);
                        hidSample = new byte[copyLen];
                        Marshal.Copy(new IntPtr(buf.ToInt64() + (long)headerSize + 8L), hidSample, 0, copyLen);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (buf != IntPtr.Zero)
                {
                    try { Marshal.FreeHGlobal(buf); } catch { }
                }
            }
        }

        public static bool TryReadRawMouseButtonFlags(IntPtr lParam, out uint rawType, out IntPtr rawDevice, out ushort buttonFlags)
        {
            rawType = 0;
            rawDevice = IntPtr.Zero;
            buttonFlags = 0;
            IntPtr buf = IntPtr.Zero;
            try
            {
                uint headerSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
                uint dwSize = 0;
                if (GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref dwSize, headerSize) != 0 || dwSize < headerSize || dwSize > 4096)
                {
                    return false;
                }
                buf = Marshal.AllocHGlobal((int)dwSize);
                if (GetRawInputData(lParam, RID_INPUT, buf, ref dwSize, headerSize) != dwSize)
                {
                    return false;
                }
                RAWINPUTHEADER hdr = (RAWINPUTHEADER)Marshal.PtrToStructure(buf, typeof(RAWINPUTHEADER));
                rawType = hdr.dwType;
                rawDevice = hdr.hDevice;
                if (hdr.dwType == RIM_TYPEMOUSE && dwSize >= headerSize + 8)
                {
                    // RAWMOUSE: usFlags (offset +0, 2B), union padding (+2, 2B), usButtonFlags (+4, 2B)
                    buttonFlags = unchecked((ushort)Marshal.ReadInt16(buf, (int)headerSize + 4));
                }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (buf != IntPtr.Zero)
                {
                    try { Marshal.FreeHGlobal(buf); } catch { }
                }
            }
        }
        #endregion

        #region WinMM (Audio)
        public const uint WAVE_MAPPER = unchecked((uint)-1);
        public const uint CALLBACK_FUNCTION = 0x00030000;
        public const uint CALLBACK_EVENT = 0x00050000;
        public const ushort WAVE_FORMAT_PCM = 1;
        public const uint WHDR_DONE = 0x00000001;
        public const uint WHDR_PREPARED = 0x00000002;

        public delegate void WaveInProc(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

        [DllImport("winmm.dll")]
        public static extern uint waveInGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        public static extern int waveInGetDevCaps(IntPtr uDeviceID, out WAVEINCAPSW pwic, uint cbwic);

        [DllImport("winmm.dll")]
        public static extern uint waveOutGetNumDevs();

        [DllImport("winmm.dll")]
        public static extern int waveOutOpen(out IntPtr phwo, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

        [DllImport("winmm.dll")]
        public static extern int waveOutPrepareHeader(IntPtr hwo, ref WAVEHDR pwh, uint cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveOutWrite(IntPtr hwo, ref WAVEHDR pwh, uint cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveOutUnprepareHeader(IntPtr hwo, ref WAVEHDR pwh, uint cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveOutReset(IntPtr hwo);

        [DllImport("winmm.dll")]
        public static extern int waveOutClose(IntPtr hwo);

        [DllImport("winmm.dll")]
        public static extern int waveInOpen(out IntPtr phwi, uint uDeviceID, ref WAVEFORMATEX pwfx, WaveInProc dwCallback, IntPtr dwInstance, uint fdwOpen);

        [DllImport("winmm.dll")]
        public static extern int waveInOpen(out IntPtr phwi, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

        [DllImport("winmm.dll")]
        public static extern int waveInPrepareHeader(IntPtr hwi, ref WAVEHDR pwh, uint cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveInAddBuffer(IntPtr hwi, ref WAVEHDR pwh, uint cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveInStart(IntPtr hwi);

        [DllImport("winmm.dll")]
        public static extern int waveInStop(IntPtr hwi);

        [DllImport("winmm.dll")]
        public static extern int waveInReset(IntPtr hwi);

        [DllImport("winmm.dll")]
        public static extern int waveInUnprepareHeader(IntPtr hwi, ref WAVEHDR pwh, uint cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveInClose(IntPtr hwi);
        #endregion

        #region CoreAudio (IAudioEndpointVolume)
        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        public class MMDeviceEnumeratorComObject { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
            int RegisterEndpointNotificationCallback(IntPtr pClient);
            int UnregisterEndpointNotificationCallback(IntPtr pClient);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice
        {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
            int GetState(out int pdwState);
        }

        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr pNotify);
            int UnregisterControlChangeNotify(IntPtr pNotify);
            int GetChannelCount(out uint pnChannelCount);
            int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
            int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
            int GetMasterVolumeLevel(out float pfLevelDB);
            int GetMasterVolumeLevelScalar(out float pfLevel);
            int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
            int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
            int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
            int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
            int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
            int VolumeStepUp(ref Guid pguidEventContext);
            int VolumeStepDown(ref Guid pguidEventContext);
            int QueryHardwareSupport(out uint pdwHardwareSupportMask);
            int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
        }
        #endregion

        #region Media Foundation (IMFSourceReader / Video)
        public const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFStartup(uint Version, uint dwFlags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateAttributes(out IMFAttributes ppMFAttributes, uint cInitialSize);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMediaType(out IMFMediaType ppMFType);

        [DllImport("mf.dll", ExactSpelling = true)]
        public static extern int MFEnumDeviceSources(IMFAttributes pAttributes, out IntPtr pppSourceAddrs, out uint pcSources);

        [DllImport("mfreadwrite.dll", ExactSpelling = true)]
        public static extern int MFCreateSourceReaderFromMediaSource(IMFMediaSource pMediaSource, IMFAttributes pAttributes, out IMFSourceReader ppSourceReader);

        [ComImport]
        [Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFAttributes
        {
            [PreserveSig] int GetItem([In] ref Guid guidKey, [In, Out] IntPtr pValue);
            [PreserveSig] int GetItemType([In] ref Guid guidKey, out int pType);
            [PreserveSig] int CompareItem([In] ref Guid guidKey, IntPtr Value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
            [PreserveSig] int Compare(IMFAttributes pTheirs, int MatchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
            [PreserveSig] int GetUINT32([In] ref Guid guidKey, out uint punValue);
            [PreserveSig] int GetUINT64([In] ref Guid guidKey, out ulong punValue);
            [PreserveSig] int GetDouble([In] ref Guid guidKey, out double pfValue);
            [PreserveSig] int GetGUID([In] ref Guid guidKey, out Guid pguidValue);
            [PreserveSig] int GetStringLength([In] ref Guid guidKey, out uint pcchLength);
            [PreserveSig] int GetString([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
            [PreserveSig] int GetAllocatedString([In] ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
            [PreserveSig] int GetBlobSize([In] ref Guid guidKey, out uint pcbBlobSize);
            [PreserveSig] int GetBlob([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
            [PreserveSig] int GetAllocatedBlob([In] ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
            [PreserveSig] int GetUnknown([In] ref Guid guidKey, [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
            [PreserveSig] int SetItem([In] ref Guid guidKey, IntPtr Value);
            [PreserveSig] int DeleteItem([In] ref Guid guidKey);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32([In] ref Guid guidKey, uint unValue);
            [PreserveSig] int SetUINT64([In] ref Guid guidKey, ulong unValue);
            [PreserveSig] int SetDouble([In] ref Guid guidKey, double fValue);
            [PreserveSig] int SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
            [PreserveSig] int SetString([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
            [PreserveSig] int SetBlob([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize);
            [PreserveSig] int SetUnknown([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint pcItems);
            [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, [In, Out] IntPtr pValue);
            [PreserveSig] int CopyAllItems(IMFAttributes pDest);
        }

        [ComImport]
        [Guid("7FEE9E9A-4A89-47A6-899C-B6A53A70FB67")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFActivate
        {
            // IMFAttributes (30 slots — moraju biti eksplicitno ponovljeni za COM vtable u C#)
            [PreserveSig] int GetItem([In] ref Guid guidKey, [In, Out] IntPtr pValue);
            [PreserveSig] int GetItemType([In] ref Guid guidKey, out int pType);
            [PreserveSig] int CompareItem([In] ref Guid guidKey, IntPtr Value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
            [PreserveSig] int Compare(IMFAttributes pTheirs, int MatchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
            [PreserveSig] int GetUINT32([In] ref Guid guidKey, out uint punValue);
            [PreserveSig] int GetUINT64([In] ref Guid guidKey, out ulong punValue);
            [PreserveSig] int GetDouble([In] ref Guid guidKey, out double pfValue);
            [PreserveSig] int GetGUID([In] ref Guid guidKey, out Guid pguidValue);
            [PreserveSig] int GetStringLength([In] ref Guid guidKey, out uint pcchLength);
            [PreserveSig] int GetString([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
            [PreserveSig] int GetAllocatedString([In] ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
            [PreserveSig] int GetBlobSize([In] ref Guid guidKey, out uint pcbBlobSize);
            [PreserveSig] int GetBlob([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
            [PreserveSig] int GetAllocatedBlob([In] ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
            [PreserveSig] int GetUnknown([In] ref Guid guidKey, [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
            [PreserveSig] int SetItem([In] ref Guid guidKey, IntPtr Value);
            [PreserveSig] int DeleteItem([In] ref Guid guidKey);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32([In] ref Guid guidKey, uint unValue);
            [PreserveSig] int SetUINT64([In] ref Guid guidKey, ulong unValue);
            [PreserveSig] int SetDouble([In] ref Guid guidKey, double fValue);
            [PreserveSig] int SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
            [PreserveSig] int SetString([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
            [PreserveSig] int SetBlob([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize);
            [PreserveSig] int SetUnknown([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint pcItems);
            [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, [In, Out] IntPtr pValue);
            [PreserveSig] int CopyAllItems(IMFAttributes pDest);

            // IMFActivate
            [PreserveSig] int ActivateObject([In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
            [PreserveSig] int ShutdownObject();
            [PreserveSig] int DetachObject();
        }

        [ComImport]
        [Guid("279A808D-AEC7-40C8-9C6B-A6B492C78A66")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFMediaSource
        {
            // IMFMediaEventGenerator (4 slots)
            [PreserveSig] int GetEvent(uint dwFlags, out IntPtr ppEvent);
            [PreserveSig] int BeginGetEvent(IntPtr pCallback, IntPtr punkState);
            [PreserveSig] int EndGetEvent(IntPtr pResult, out IntPtr ppEvent);
            [PreserveSig] int QueueEvent(uint met, [In] ref Guid guidExtendedType, int hrStatus, IntPtr pvValue);

            // IMFMediaSource (6 slots)
            [PreserveSig] int GetCharacteristics(out uint pdwCharacteristics);
            [PreserveSig] int CreatePresentationDescriptor(out IntPtr ppPresentationDescriptor);
            [PreserveSig] int Start(IntPtr pPresentationDescriptor, [In] ref Guid pguidTimeFormat, IntPtr pvarStartPosition);
            [PreserveSig] int Stop();
            [PreserveSig] int Pause();
            [PreserveSig] int Shutdown();
        }

        [ComImport]
        [Guid("70AE66F2-C809-4E4F-8915-BDCB406B7993")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFSourceReader
        {
            [PreserveSig] int GetStreamSelection(uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] out bool pfSelected);
            [PreserveSig] int SetStreamSelection(uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] bool fSelected);
            [PreserveSig] int GetNativeMediaType(uint dwStreamIndex, uint dwMediaTypeIndex, out IMFMediaType ppMediaType);
            [PreserveSig] int GetCurrentMediaType(uint dwStreamIndex, out IMFMediaType ppMediaType);
            [PreserveSig] int SetCurrentMediaType(uint dwStreamIndex, IntPtr pdwReserved, IMFMediaType pMediaType);
            [PreserveSig] int SetCurrentPosition([In] ref Guid guidTimeFormat, IntPtr varPosition);
            [PreserveSig] int ReadSample(uint dwStreamIndex, uint dwControlFlags, out uint pdwActualStreamIndex, out uint pdwStreamFlags, out ulong pllTimestamp, out IMFSample ppSample);
            [PreserveSig] int Flush(uint dwStreamIndex);
            [PreserveSig] int GetServiceForStream(uint dwStreamIndex, [In] ref Guid guidService, [In] ref Guid riid, out IntPtr ppvObject);
            [PreserveSig] int GetPresentationAttribute(uint dwStreamIndex, [In] ref Guid guidAttribute, IntPtr pvarAttribute);
        }

        [ComImport]
        [Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFMediaType
        {
            // IMFAttributes (30 slots)
            [PreserveSig] int GetItem([In] ref Guid guidKey, [In, Out] IntPtr pValue);
            [PreserveSig] int GetItemType([In] ref Guid guidKey, out int pType);
            [PreserveSig] int CompareItem([In] ref Guid guidKey, IntPtr Value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
            [PreserveSig] int Compare(IMFAttributes pTheirs, int MatchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
            [PreserveSig] int GetUINT32([In] ref Guid guidKey, out uint punValue);
            [PreserveSig] int GetUINT64([In] ref Guid guidKey, out ulong punValue);
            [PreserveSig] int GetDouble([In] ref Guid guidKey, out double pfValue);
            [PreserveSig] int GetGUID([In] ref Guid guidKey, out Guid pguidValue);
            [PreserveSig] int GetStringLength([In] ref Guid guidKey, out uint pcchLength);
            [PreserveSig] int GetString([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
            [PreserveSig] int GetAllocatedString([In] ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
            [PreserveSig] int GetBlobSize([In] ref Guid guidKey, out uint pcbBlobSize);
            [PreserveSig] int GetBlob([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
            [PreserveSig] int GetAllocatedBlob([In] ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
            [PreserveSig] int GetUnknown([In] ref Guid guidKey, [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
            [PreserveSig] int SetItem([In] ref Guid guidKey, IntPtr Value);
            [PreserveSig] int DeleteItem([In] ref Guid guidKey);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32([In] ref Guid guidKey, uint unValue);
            [PreserveSig] int SetUINT64([In] ref Guid guidKey, ulong unValue);
            [PreserveSig] int SetDouble([In] ref Guid guidKey, double fValue);
            [PreserveSig] int SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
            [PreserveSig] int SetString([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
            [PreserveSig] int SetBlob([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize);
            [PreserveSig] int SetUnknown([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint pcItems);
            [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, [In, Out] IntPtr pValue);
            [PreserveSig] int CopyAllItems(IMFAttributes pDest);

            // IMFMediaType
            [PreserveSig] int GetMajorType(out Guid pguidMajorType);
            [PreserveSig] int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool pfCompressed);
            [PreserveSig] int IsEqual(IMFMediaType pIMediaType, out uint pdwFlags);
            [PreserveSig] int GetRepresentation(Guid guidRepresentation, out IntPtr ppvRepresentation);
            [PreserveSig] int FreeRepresentation(Guid guidRepresentation, IntPtr pvRepresentation);
        }

        [ComImport]
        [Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFSample
        {
            // IMFAttributes (30 slots)
            [PreserveSig] int GetItem([In] ref Guid guidKey, [In, Out] IntPtr pValue);
            [PreserveSig] int GetItemType([In] ref Guid guidKey, out int pType);
            [PreserveSig] int CompareItem([In] ref Guid guidKey, IntPtr Value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
            [PreserveSig] int Compare(IMFAttributes pTheirs, int MatchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
            [PreserveSig] int GetUINT32([In] ref Guid guidKey, out uint punValue);
            [PreserveSig] int GetUINT64([In] ref Guid guidKey, out ulong punValue);
            [PreserveSig] int GetDouble([In] ref Guid guidKey, out double pfValue);
            [PreserveSig] int GetGUID([In] ref Guid guidKey, out Guid pguidValue);
            [PreserveSig] int GetStringLength([In] ref Guid guidKey, out uint pcchLength);
            [PreserveSig] int GetString([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
            [PreserveSig] int GetAllocatedString([In] ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
            [PreserveSig] int GetBlobSize([In] ref Guid guidKey, out uint pcbBlobSize);
            [PreserveSig] int GetBlob([In] ref Guid guidKey, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
            [PreserveSig] int GetAllocatedBlob([In] ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
            [PreserveSig] int GetUnknown([In] ref Guid guidKey, [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
            [PreserveSig] int SetItem([In] ref Guid guidKey, IntPtr Value);
            [PreserveSig] int DeleteItem([In] ref Guid guidKey);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32([In] ref Guid guidKey, uint unValue);
            [PreserveSig] int SetUINT64([In] ref Guid guidKey, ulong unValue);
            [PreserveSig] int SetDouble([In] ref Guid guidKey, double fValue);
            [PreserveSig] int SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
            [PreserveSig] int SetString([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
            [PreserveSig] int SetBlob([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize);
            [PreserveSig] int SetUnknown([In] ref Guid guidKey, [In, MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint pcItems);
            [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, [In, Out] IntPtr pValue);
            [PreserveSig] int CopyAllItems(IMFAttributes pDest);

            // IMFSample
            [PreserveSig] int GetSampleFlags(out uint pdwSampleFlags);
            [PreserveSig] int SetSampleFlags(uint dwSampleFlags);
            [PreserveSig] int GetSampleTime(out long pllSampleTime);
            [PreserveSig] int SetSampleTime(long llSampleTime);
            [PreserveSig] int GetSampleDuration(out long pllSampleDuration);
            [PreserveSig] int SetSampleDuration(long llSampleDuration);
            [PreserveSig] int GetBufferCount(out uint pdwBufferCount);
            [PreserveSig] int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
            [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
            [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
            [PreserveSig] int RemoveBufferByIndex(uint dwIndex);
            [PreserveSig] int RemoveAllBuffers();
            [PreserveSig] int GetTotalLength(out uint pcbTotalLength);
            [PreserveSig] int CopyToBuffer(IMFMediaBuffer pBuffer);
        }

        [ComImport]
        [Guid("045FA593-8799-42B8-BC8D-8968C6453507")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
            [PreserveSig] int Unlock();
            [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
            [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
            [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
        }

        public static readonly Guid IID_IMFMediaSource = new Guid("279A808D-AEC7-40C8-9C6B-A6B492C78A66");
        public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE = new Guid("C60AC5FE-252A-478F-A0EF-BC8FA5F7CAD3");
        public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID = new Guid("8AC3587A-4AE7-42D8-99E0-0A6013EEF90F");
        public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_CATEGORY = new Guid("77F0AE69-C3BD-4509-941D-467E4D24899E");
        public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK = new Guid("58F0AAD8-22BF-4F8A-BB3D-D2C4978C6E2F");
        public static readonly Guid KSCATEGORY_SENSOR_CAMERA = new Guid("24E552D7-6523-47F7-A647-D3465BF1F5CA");
        public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME = new Guid("60D0E559-52F8-4FA2-BBCE-ACDB34A8EC01");
        public static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
        public static readonly Guid MF_MT_SUBTYPE = new Guid("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
        public static readonly Guid MF_MT_FRAME_SIZE = new Guid("1652C33D-D6B2-4012-B834-72030849A37D");
        public static readonly Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_NV12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_YUY2 = new Guid("32595559-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_L8 = new Guid("00000032-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_L16 = new Guid("00000051-0000-0010-8000-00AA00389B71");
        public static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new Guid("FB394F3D-CCF1-42EE-BBB3-F9B845D5681D");
        #endregion

        #region USB Device Location (cfgmgr32.dll)
        public const int DBT_DEVNODES_CHANGED = 0x0007;
        private const uint CM_DRP_DEVICEDESC = 0x00000001;
        private const uint CM_DRP_SERVICE = 0x00000005;
        private const uint CM_DRP_CLASS = 0x00000008;
        private const uint CM_DRP_FRIENDLYNAME = 0x0000000C;
        private const uint CM_DRP_LOCATION_INFORMATION = 0x0000000E;
        private const uint CM_DRP_ADDRESS = 0x0000001D;
        private const uint CM_DRP_LOCATION_PATHS = 0x00000024;

        // Windows SDK cfg.h DevNode status flegovi
        public const uint DN_STARTED = 0x00000008u;
        public const uint DN_HAS_PROBLEM = 0x00000400u;
        public const uint DN_WILL_BE_REMOVED = 0x00040000u;

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        public static extern int CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        public static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_IDW(uint dnDevInst, System.Text.StringBuilder Buffer, uint BufferLen, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_DevNode_Registry_PropertyW(
            uint dnDevInst,
            uint ulProperty,
            out uint pulRegDataType,
            byte[] Buffer,
            ref uint pulLength,
            uint ulFlags);

        private static string ReadDevNodePropertyString(uint devInst, uint property)
        {
            try
            {
                uint regType = 0;
                uint len = 1024;
                byte[] buf = new byte[len];
                int cr = CM_Get_DevNode_Registry_PropertyW(devInst, property, out regType, buf, ref len, 0);
                if (cr != 0 || len == 0) return null;
                if (len > (uint)buf.Length) len = (uint)buf.Length;

                string s = System.Text.Encoding.Unicode.GetString(buf, 0, (int)len);
                int nullIdx = s.IndexOf('\0');
                if (nullIdx >= 0) s = s.Substring(0, nullIdx);
                return s.Trim();
            }
            catch { return null; }
        }

        private static uint ReadDevNodeAddress(uint devInst)
        {
            try
            {
                uint regType = 0;
                uint len = 4;
                byte[] buf = new byte[4];
                int cr = CM_Get_DevNode_Registry_PropertyW(devInst, CM_DRP_ADDRESS, out regType, buf, ref len, 0);
                if (cr == 0 && len >= 4)
                {
                    return BitConverter.ToUInt32(buf, 0);
                }
            }
            catch { }
            return 0;
        }

        private static string GetDevNodeDeviceId(uint devInst)
        {
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder(512);
                if (CM_Get_Device_IDW(devInst, sb, (uint)sb.Capacity, 0) == 0)
                {
                    return sb.ToString();
                }
            }
            catch { }
            return null;
        }

        public static string ConvertRawDevicePathToPnpInstanceId(string rawDeviceName)
        {
            if (string.IsNullOrEmpty(rawDeviceName)) return "";
            string s = rawDeviceName.Trim();
            if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s.Substring(4);
            else if (s.StartsWith(@"\??\", StringComparison.Ordinal)) s = s.Substring(4);

            int guidIdx = s.LastIndexOf("#{", StringComparison.Ordinal);
            if (guidIdx > 0)
            {
                s = s.Substring(0, guidIdx);
            }
            return s.Replace('#', '\\');
        }

        public static bool TryGetRawInputPnpHierarchy(
            string rawDeviceName,
            out string leafPnpId,
            out System.Collections.Generic.List<string> ancestryIds,
            out string deviceDesc,
            out string serviceName)
        {
            leafPnpId = ConvertRawDevicePathToPnpInstanceId(rawDeviceName);
            ancestryIds = new System.Collections.Generic.List<string>();
            deviceDesc = "";
            serviceName = "";
            if (string.IsNullOrEmpty(leafPnpId)) return false;

            try
            {
                uint devInst = 0;
                if (CM_Locate_DevNodeW(out devInst, leafPnpId, 0) != 0 || devInst == 0)
                {
                    return false;
                }

                ancestryIds.Add(leafPnpId);
                string desc = ReadDevNodePropertyString(devInst, CM_DRP_FRIENDLYNAME);
                if (string.IsNullOrEmpty(desc)) desc = ReadDevNodePropertyString(devInst, CM_DRP_DEVICEDESC);
                if (!string.IsNullOrEmpty(desc)) deviceDesc = desc;
                string svc = ReadDevNodePropertyString(devInst, CM_DRP_SERVICE);
                if (!string.IsNullOrEmpty(svc)) serviceName = svc;

                uint curr = devInst;
                for (int depth = 0; depth < 10; depth++)
                {
                    uint parent = 0;
                    if (CM_Get_Parent(out parent, curr, 0) != 0 || parent == 0) break;
                    string pid = GetDevNodeDeviceId(parent);
                    if (!string.IsNullOrEmpty(pid))
                    {
                        ancestryIds.Add(pid);
                        if (string.IsNullOrEmpty(deviceDesc))
                        {
                            string pDesc = ReadDevNodePropertyString(parent, CM_DRP_FRIENDLYNAME);
                            if (string.IsNullOrEmpty(pDesc)) pDesc = ReadDevNodePropertyString(parent, CM_DRP_DEVICEDESC);
                            if (!string.IsNullOrEmpty(pDesc)) deviceDesc = pDesc;
                        }
                        if (string.IsNullOrEmpty(serviceName))
                        {
                            string pSvc = ReadDevNodePropertyString(parent, CM_DRP_SERVICE);
                            if (!string.IsNullOrEmpty(pSvc)) serviceName = pSvc;
                        }
                    }
                    curr = parent;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Proverava da li je USB PnP čvor trenutno prisutan u stablu, startovan (DN_STARTED),
        /// bez najavljenog uklanjanja (DN_WILL_BE_REMOVED) i bez greške (DN_HAS_PROBLEM / problem == 0).
        /// </summary>
        public static bool IsUsbDevNodeLiveAndStarted(string pnpDeviceId, out string statusDiag)
        {
            statusDiag = "";
            if (string.IsNullOrEmpty(pnpDeviceId))
            {
                statusDiag = "pnpDeviceId=empty";
                return false;
            }
            try
            {
                uint devInst = 0;
                int crLocate = CM_Locate_DevNodeW(out devInst, pnpDeviceId, 0);
                if (crLocate != 0 || devInst == 0)
                {
                    statusDiag = string.Format("CR=0x{0:X} status=0x00000000 problem=0 (devInst={1})", crLocate, devInst);
                    return false;
                }

                uint status = 0;
                uint problem = 0;
                int crStatus = CM_Get_DevNode_Status(out status, out problem, devInst, 0);
                statusDiag = string.Format("CR=0x{0:X} status=0x{1:X8} problem={2}", crStatus, status, problem);
                if (crStatus != 0) return false;
                if ((status & DN_STARTED) == 0u) return false;
                if ((status & DN_WILL_BE_REMOVED) != 0u) return false;
                if ((status & DN_HAS_PROBLEM) != 0u || problem != 0u) return false;
                return true;
            }
            catch (Exception ex)
            {
                statusDiag = "CR=EX err=" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Pronalazi jedinstvenu fizicku putanju USB porta penjanjem kroz PnP stablo do USB\... parent cvora.
        /// Vraća čist kanonski ključ porta bez dodavanja prikaznog sufiksa.
        /// </summary>
        public static string ResolveUsbPhysicalPortLocation(string pnpDeviceId, out string debugInfo)
        {
            string dummyDisplay;
            return ResolveUsbPhysicalPortLocation(pnpDeviceId, out debugInfo, out dummyDisplay);
        }

        /// <summary>
        /// Pronalazi jedinstvenu fizicku putanju USB porta penjanjem kroz PnP stablo do USB\... parent cvora.
        /// Spasava situaciju kada USBSTOR\DISK nema LocationPaths ili je prazan u WMI.
        /// Kanonski ključ se vraća čist (bez sufiksa), dok se usbLocInfo vraća odvojeno kroz displayLocationInfo za UI prikaz.
        /// </summary>
        public static string ResolveUsbPhysicalPortLocation(string pnpDeviceId, out string debugInfo, out string displayLocationInfo)
        {
            debugInfo = "";
            displayLocationInfo = "";
            if (string.IsNullOrEmpty(pnpDeviceId)) return null;
            try
            {
                uint devInst = 0;
                if (CM_Locate_DevNodeW(out devInst, pnpDeviceId, 0) != 0 || devInst == 0)
                {
                    debugInfo = "CM_Locate_DevNodeW failed for " + pnpDeviceId;
                    return null;
                }

                string leafLocPaths = ReadDevNodePropertyString(devInst, CM_DRP_LOCATION_PATHS);
                string leafLocInfo = ReadDevNodePropertyString(devInst, CM_DRP_LOCATION_INFORMATION);
                uint leafAddr = ReadDevNodeAddress(devInst);

                uint curr = devInst;
                string usbNodeId = null;
                string usbLocPaths = null;
                string usbLocInfo = null;
                uint usbAddr = 0;
                string hubNodeId = null;

                bool leafIsUsbDevice = (pnpDeviceId.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase) ||
                                        pnpDeviceId.StartsWith("USB4\\VID_", StringComparison.OrdinalIgnoreCase));
                if (leafIsUsbDevice)
                {
                    usbNodeId = pnpDeviceId;
                    usbLocPaths = leafLocPaths;
                    usbLocInfo = leafLocInfo;
                    usbAddr = leafAddr;
                    uint hubParent = 0;
                    if (CM_Get_Parent(out hubParent, devInst, 0) == 0 && hubParent != 0)
                    {
                        hubNodeId = GetDevNodeDeviceId(hubParent);
                    }
                }
                else
                {
                    for (int depth = 0; depth < 12; depth++)
                    {
                        uint parent = 0;
                        if (CM_Get_Parent(out parent, curr, 0) != 0 || parent == 0) break;
                        string pid = GetDevNodeDeviceId(parent);
                        if (!string.IsNullOrEmpty(pid) &&
                            (pid.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
                             pid.StartsWith("USB4\\", StringComparison.OrdinalIgnoreCase)) &&
                            pid.IndexOf("ROOT_HUB", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            usbNodeId = pid;
                            usbLocPaths = ReadDevNodePropertyString(parent, CM_DRP_LOCATION_PATHS);
                            usbLocInfo = ReadDevNodePropertyString(parent, CM_DRP_LOCATION_INFORMATION);
                            usbAddr = ReadDevNodeAddress(parent);

                            uint hubParent = 0;
                            if (CM_Get_Parent(out hubParent, parent, 0) == 0 && hubParent != 0)
                            {
                                hubNodeId = GetDevNodeDeviceId(hubParent);
                            }
                            break;
                        }
                        curr = parent;
                    }
                }

                displayLocationInfo = !string.IsNullOrEmpty(usbLocInfo) ? usbLocInfo : (leafLocInfo ?? "");
                debugInfo = string.Format(
                    "leafPaths='{0}' leafInfo='{1}' usbNode='{2}' usbPaths='{3}' usbInfo='{4}' addr={5} hub='{6}'",
                    leafLocPaths ?? "", leafLocInfo ?? "", usbNodeId ?? "", usbLocPaths ?? "", usbLocInfo ?? "", usbAddr, hubNodeId ?? "");

                if (!string.IsNullOrEmpty(usbLocPaths))
                {
                    return usbLocPaths;
                }
                if (!string.IsNullOrEmpty(leafLocPaths))
                {
                    return leafLocPaths;
                }
                if (!string.IsNullOrEmpty(usbLocInfo))
                {
                    return (!string.IsNullOrEmpty(hubNodeId) ? hubNodeId + " | " : "") + usbLocInfo + "|ADDR=" + usbAddr;
                }
                if (!string.IsNullOrEmpty(hubNodeId) && usbAddr > 0)
                {
                    return hubNodeId + "|ADDR=" + usbAddr;
                }
                if (!string.IsNullOrEmpty(leafLocInfo))
                {
                    return leafLocInfo;
                }
            }
            catch (Exception ex)
            {
                debugInfo = "ResolveUsbPhysicalPortLocation ex: " + ex.Message;
            }
            return null;
        }
        #endregion

        #region Windows Biometric Framework (winbio.dll)
        public const uint WINBIO_TYPE_FINGERPRINT = 0x00000008;
        public const uint WINBIO_POOL_SYSTEM = 1;
        public const uint WINBIO_POOL_PRIVATE = 2;
        public const uint WINBIO_FLAG_DEFAULT = 0x00000000;
        public const uint WINBIO_FLAG_RAW = 0x00000001;
        public const uint WINBIO_FLAG_BASIC = 0x00010000;
        public const byte WINBIO_NO_PURPOSE_AVAILABLE = 0x00;
        public const byte WINBIO_PURPOSE_VERIFY = 0x01;
        public const byte WINBIO_PURPOSE_ENROLL = 0x02;
        public const byte WINBIO_DATA_FLAG_PROCESSED = 0x01;
        public const byte WINBIO_DATA_FLAG_RAW = 0x20;

        public const int WINBIO_E_UNKNOWN_ID = unchecked((int)0x80098003);
        public const int WINBIO_E_CANCELED = unchecked((int)0x80098004);
        public const int WINBIO_E_NO_MATCH = unchecked((int)0x80098005);
        public const int WINBIO_E_BAD_CAPTURE = unchecked((int)0x80098008);

        [StructLayout(LayoutKind.Sequential)]
        public struct WINBIO_VERSION
        {
            public uint MajorVersion;
            public uint MinorVersion;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WINBIO_UNIT_SCHEMA
        {
            public uint UnitId;
            public uint PoolType;
            public uint BiometricFactor;
            public uint SensorSubType;
            public uint Capabilities;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string DeviceInstanceId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string Description;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string Manufacturer;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string Model;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string SerialNumber;
            public WINBIO_VERSION FirmwareVersion;
        }

        [DllImport("winbio.dll")]
        public static extern int WinBioEnumBiometricUnits(
            uint Factor,
            out IntPtr UnitSchemaArray,
            out UIntPtr UnitCount);

        [DllImport("winbio.dll")]
        public static extern int WinBioOpenSession(
            uint Factor,
            uint PoolType,
            uint Flags,
            IntPtr UnitArray,
            UIntPtr UnitCount,
            IntPtr DatabaseId,
            out uint SessionHandle);

        [DllImport("winbio.dll")]
        public static extern int WinBioCloseSession(uint SessionHandle);

        [DllImport("winbio.dll")]
        public static extern int WinBioCancel(uint SessionHandle);

        [DllImport("winbio.dll")]
        public static extern int WinBioFree(IntPtr Address);

        [DllImport("winbio.dll")]
        public static extern int WinBioAcquireFocus();

        [DllImport("winbio.dll")]
        public static extern int WinBioReleaseFocus();

        [DllImport("winbio.dll")]
        public static extern int WinBioLocateSensor(
            uint SessionHandle,
            out uint UnitId);

        [DllImport("winbio.dll")]
        public static extern int WinBioIdentify(
            uint SessionHandle,
            out uint UnitId,
            IntPtr Identity,
            out byte SubFactor,
            out uint RejectDetail);

        [DllImport("winbio.dll")]
        public static extern int WinBioCaptureSample(
            uint SessionHandle,
            byte Purpose,
            byte Flags,
            out uint UnitId,
            out IntPtr Sample,
            out UIntPtr SampleSize,
            out uint RejectDetail);
        #endregion

        #region Memory & Unbuffered Disk IO
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;
        public const uint CREATE_ALWAYS = 2;
        public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
        public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
        public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
        public const uint FILE_FLAG_RANDOM_ACCESS = 0x10000000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadFile(
            IntPtr hFile,
            IntPtr lpBuffer,
            uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFilePointerEx(
            IntPtr hFile,
            long liDistanceToMove,
            out long lpNewFilePointer,
            uint dwMoveMethod);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            ref uint lpInBuffer,
            uint nInBufferSize,
            ref uint lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);
        #endregion

        #region WlanApi
        public const uint wlan_intf_opcode_radio_state = 4u; // Windows SDK wlanapi.h: WLAN_INTF_OPCODE.wlan_intf_opcode_radio_state
        public const uint wlan_intf_opcode_current_connection = 7u;
        public const uint wlan_intf_opcode_channel_number = 8u;
        public const uint WLAN_INTERFACE_STATE_CONNECTED = 1u;
        public const uint ERROR_ACCESS_DENIED = 5u;

        public enum DOT11_RADIO_STATE : uint
        {
            dot11_radio_state_unknown = 0,
            dot11_radio_state_on = 1,
            dot11_radio_state_off = 2
        }

        public enum DOT11_BSS_TYPE : uint
        {
            dot11_BSS_type_infrastructure = 1,
            dot11_BSS_type_independent = 2,
            dot11_BSS_type_any = 3
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_PHY_RADIO_STATE
        {
            public uint dwPhyIndex;
            public uint dot11SoftwareRadioState;
            public uint dot11HardwareRadioState;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_RADIO_STATE
        {
            public uint dwNumberOfPhys;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public WLAN_PHY_RADIO_STATE[] PhyRadioState;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DOT11_SSID
        {
            public uint uSSIDLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] ucSSID;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_RATE_SET
        {
            public uint uRateSetLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)]
            public ushort[] usRateSet;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_BSS_ENTRY
        {
            public DOT11_SSID dot11Ssid;
            public uint uPhyId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[] dot11Bssid;
            public uint dot11BssType;
            public uint dot11BssPhyType;
            public int lRssi;
            public uint uLinkQuality;
            public byte bInRegDomain;
            public ushort usBeaconPeriod;
            public ulong ullTimestamp;
            public ulong ullHostTimestamp;
            public ushort usCapabilityInformation;
            public uint ulChCenterFrequency;
            public WLAN_RATE_SET wlanRateSet;
            public uint ulIeOffset;
            public uint ulIeSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_BSS_LIST
        {
            public uint dwTotalSize;
            public uint dwNumberOfItems;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_ASSOCIATION_ATTRIBUTES
        {
            public DOT11_SSID dot11Ssid;
            public uint dot11BssType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[] dot11Bssid;
            public uint dot11PhyType;
            public uint uDot11PhyIndex;
            public uint wlanSignalQuality;
            public uint ulRxRate;
            public uint ulTxRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_SECURITY_ATTRIBUTES
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool bSecurityEnabled;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bOneXEnabled;
            public uint dot11AuthAlgorithm;
            public uint dot11CipherAlgorithm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_CONNECTION_ATTRIBUTES
        {
            public uint isState;
            public uint wlanConnectionMode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strProfileName;
            public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
            public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
        }

        public class WlanCurrentConnectionInfo
        {
            public bool Connected { get; set; }
            public string Ssid { get; set; }
            public int SignalPct { get; set; }
            public uint PhyType { get; set; }
            public uint ChannelNumber { get; set; }
            public uint RxRateKbps { get; set; }
            public uint TxRateKbps { get; set; }
            public uint LastError { get; set; }
        }

        public class WlanBssScanSummary
        {
            public bool ApiSucceeded { get; set; }
            public bool AccessDenied { get; set; }
            public uint LastError { get; set; }
            public int UniqueSsidCount { get; set; }
            public int BssidCount { get; set; }
            public int Band24Count { get; set; }
            public int Band5Count { get; set; }
            public int Band6Count { get; set; }
            public int MaxSignalPct { get; set; }
            public int MaxRssiDbm { get; set; }
            public bool HasRssi { get; set; }
            public int Max24Pct { get; set; }
            public int Max5Pct { get; set; }
            public int Max6Pct { get; set; }
            public bool ServiceSsidFound { get; set; }
            public int ServiceSignalPct { get; set; }
            public int ServiceRssiDbm { get; set; }
            public bool HasServiceRssi { get; set; }
        }

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanOpenHandle(
            uint dwClientVersion,
            IntPtr pReserved,
            out uint pdwNegotiatedVersion,
            out IntPtr phClientHandle);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanCloseHandle(
            IntPtr hClientHandle,
            IntPtr pReserved);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanEnumInterfaces(
            IntPtr hClientHandle,
            IntPtr pReserved,
            out IntPtr ppInterfaceList);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanScan(
            IntPtr hClientHandle,
            ref Guid pInterfaceGuid,
            IntPtr pDot11Ssid,
            IntPtr pIeData,
            IntPtr pReserved);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanQueryInterface(
            IntPtr hClientHandle,
            ref Guid pInterfaceGuid,
            uint OpCode,
            IntPtr pReserved,
            out uint pdwDataSize,
            out IntPtr ppData,
            out uint pWlanOpcodeValueType);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanGetNetworkBssList(
            IntPtr hClientHandle,
            ref Guid pInterfaceGuid,
            IntPtr pDot11Ssid,
            uint dot11BssType,
            [MarshalAs(UnmanagedType.Bool)] bool bSecurityEnabled,
            IntPtr pReserved,
            out IntPtr ppWlanBssList);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern void WlanFreeMemory(IntPtr pMemory);

        private static System.Collections.Generic.List<Guid> CollectWlanInterfaceGuids(IntPtr pIfList)
        {
            var physical = new System.Collections.Generic.List<Guid>();
            var all = new System.Collections.Generic.List<Guid>();
            if (pIfList == IntPtr.Zero) return physical;

            int count = Marshal.ReadInt32(pIfList, 0);
            if (count < 0 || count > 64) count = 0;
            const int wlanInterfaceInfoSize = 532; // Guid (16) + WCHAR[256] (512) + State (4)
            for (int i = 0; i < count; i++)
            {
                IntPtr itemPtr = new IntPtr(pIfList.ToInt64() + 8L + ((long)i * wlanInterfaceInfoSize));
                byte[] guidBytes = new byte[16];
                Marshal.Copy(itemPtr, guidBytes, 0, 16);
                Guid ifaceGuid = new Guid(guidBytes);
                all.Add(ifaceGuid);

                string desc = "";
                try
                {
                    desc = Marshal.PtrToStringUni(new IntPtr(itemPtr.ToInt64() + 16L), 256) ?? "";
                    int nul = desc.IndexOf('\0');
                    if (nul >= 0) desc = desc.Substring(0, nul);
                }
                catch { }

                bool isVirtual = desc.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 desc.IndexOf("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 desc.IndexOf("Hosted", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isVirtual)
                {
                    physical.Add(ifaceGuid);
                }
            }
            return physical.Count > 0 ? physical : all;
        }

        public static bool QueryWifiRadioState(out bool queried, out bool hwRadioOff, out bool swRadioOff)
        {
            queried = false;
            hwRadioOff = false;
            swRadioOff = false;

            IntPtr clientHandle = IntPtr.Zero;
            IntPtr pIfList = IntPtr.Zero;
            try
            {
                uint negotiatedVersion;
                if (WlanOpenHandle(2u, IntPtr.Zero, out negotiatedVersion, out clientHandle) != 0u || clientHandle == IntPtr.Zero)
                {
                    return false;
                }
                if (WlanEnumInterfaces(clientHandle, IntPtr.Zero, out pIfList) != 0u || pIfList == IntPtr.Zero)
                {
                    return false;
                }

                System.Collections.Generic.List<Guid> ifaces = CollectWlanInterfaceGuids(pIfList);
                uint phyStructSize = (uint)Marshal.SizeOf(typeof(WLAN_PHY_RADIO_STATE));

                for (int i = 0; i < ifaces.Count; i++)
                {
                    Guid ifaceGuid = ifaces[i];
                    IntPtr pRadioData = IntPtr.Zero;
                    try
                    {
                        uint dataSize;
                        uint opcodeValueType;
                        uint err = WlanQueryInterface(
                            clientHandle,
                            ref ifaceGuid,
                            wlan_intf_opcode_radio_state,
                            IntPtr.Zero,
                            out dataSize,
                            out pRadioData,
                            out opcodeValueType);

                        if (err == 0u && pRadioData != IntPtr.Zero && dataSize >= 4u)
                        {
                            uint numPhys = unchecked((uint)Marshal.ReadInt32(pRadioData, 0));
                            if (numPhys > 64u) numPhys = 64u;
                            if (numPhys > 0u && dataSize >= (4u + numPhys * phyStructSize))
                            {
                                queried = true;
                                bool ifaceHwOff = false;
                                bool ifaceHwOn = false;
                                bool ifaceSwOff = false;
                                bool ifaceSwOn = false;

                                for (uint p = 0; p < numPhys; p++)
                                {
                                    IntPtr phyPtr = new IntPtr(pRadioData.ToInt64() + 4L + ((long)p * phyStructSize));
                                    WLAN_PHY_RADIO_STATE phy = (WLAN_PHY_RADIO_STATE)Marshal.PtrToStructure(phyPtr, typeof(WLAN_PHY_RADIO_STATE));
                                    if (phy.dot11HardwareRadioState == (uint)DOT11_RADIO_STATE.dot11_radio_state_off)
                                        ifaceHwOff = true;
                                    else if (phy.dot11HardwareRadioState == (uint)DOT11_RADIO_STATE.dot11_radio_state_on)
                                        ifaceHwOn = true;

                                    if (phy.dot11SoftwareRadioState == (uint)DOT11_RADIO_STATE.dot11_radio_state_off)
                                        ifaceSwOff = true;
                                    else if (phy.dot11SoftwareRadioState == (uint)DOT11_RADIO_STATE.dot11_radio_state_on)
                                        ifaceSwOn = true;
                                }

                                if (ifaceHwOff && !ifaceHwOn) hwRadioOff = true;
                                if (ifaceSwOff && !ifaceSwOn) swRadioOff = true;
                            }
                        }
                    }
                    finally
                    {
                        if (pRadioData != IntPtr.Zero)
                        {
                            try { WlanFreeMemory(pRadioData); } catch { }
                        }
                    }
                }
                return queried;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (pIfList != IntPtr.Zero)
                {
                    try { WlanFreeMemory(pIfList); } catch { }
                }
                if (clientHandle != IntPtr.Zero)
                {
                    try { WlanCloseHandle(clientHandle, IntPtr.Zero); } catch { }
                }
            }
        }

        public static WlanBssScanSummary GetWlanBssScanSummary(string targetServiceSsid)
        {
            WlanBssScanSummary summary = new WlanBssScanSummary();
            IntPtr clientHandle = IntPtr.Zero;
            IntPtr pIfList = IntPtr.Zero;

            try
            {
                uint negotiatedVersion;
                uint openErr = WlanOpenHandle(2u, IntPtr.Zero, out negotiatedVersion, out clientHandle);
                if (openErr != 0u || clientHandle == IntPtr.Zero)
                {
                    summary.LastError = openErr;
                    return summary;
                }

                uint enumErr = WlanEnumInterfaces(clientHandle, IntPtr.Zero, out pIfList);
                if (enumErr != 0u || pIfList == IntPtr.Zero)
                {
                    summary.LastError = enumErr;
                    return summary;
                }

                System.Collections.Generic.List<Guid> ifaces = CollectWlanInterfaceGuids(pIfList);
                var uniqueSsids = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                var seenBssids = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int entrySize = Marshal.SizeOf(typeof(WLAN_BSS_ENTRY));

                for (int i = 0; i < ifaces.Count; i++)
                {
                    Guid ifaceGuid = ifaces[i];
                    IntPtr pBssList = IntPtr.Zero;
                    try
                    {
                        uint bssErr = WlanGetNetworkBssList(
                            clientHandle,
                            ref ifaceGuid,
                            IntPtr.Zero,
                            (uint)DOT11_BSS_TYPE.dot11_BSS_type_any,
                            false,
                            IntPtr.Zero,
                            out pBssList);

                        if (bssErr == ERROR_ACCESS_DENIED)
                        {
                            summary.AccessDenied = true;
                            summary.LastError = bssErr;
                            continue;
                        }
                        if (bssErr != 0u || pBssList == IntPtr.Zero)
                        {
                            summary.LastError = bssErr;
                            continue;
                        }

                        summary.ApiSucceeded = true;
                        uint totalSize = unchecked((uint)Marshal.ReadInt32(pBssList, 0));
                        uint numItems = unchecked((uint)Marshal.ReadInt32(pBssList, 4));
                        if (totalSize < 8u || numItems == 0u) continue;
                        if (numItems > 2048u) numItems = 2048u;

                        for (uint j = 0; j < numItems; j++)
                        {
                            long relOffset = 8L + ((long)j * entrySize);
                            if (relOffset + entrySize > (long)totalSize) break;

                            IntPtr entryPtr = new IntPtr(pBssList.ToInt64() + relOffset);
                            WLAN_BSS_ENTRY entry = (WLAN_BSS_ENTRY)Marshal.PtrToStructure(entryPtr, typeof(WLAN_BSS_ENTRY));

                            string bssidKey = "";
                            if (entry.dot11Bssid != null && entry.dot11Bssid.Length == 6)
                            {
                                bssidKey = BitConverter.ToString(entry.dot11Bssid) + "|" + entry.ulChCenterFrequency;
                            }
                            if (!string.IsNullOrEmpty(bssidKey) && !seenBssids.Add(bssidKey))
                            {
                                continue;
                            }

                            string ssid = "";
                            int ssidLen = (int)Math.Min(entry.dot11Ssid.uSSIDLength, 32u);
                            if (ssidLen > 0 && entry.dot11Ssid.ucSSID != null)
                            {
                                int safeLen = Math.Min(ssidLen, entry.dot11Ssid.ucSSID.Length);
                                ssid = System.Text.Encoding.UTF8.GetString(entry.dot11Ssid.ucSSID, 0, safeLen).TrimEnd('\0');
                            }
                            if (!string.IsNullOrEmpty(ssid))
                            {
                                uniqueSsids.Add(ssid);
                            }

                            summary.BssidCount++;
                            int pct = (int)Math.Min(100u, entry.uLinkQuality);
                            int rssi = entry.lRssi;
                            bool validRssi = (rssi < 0 && rssi >= -120);

                            if (pct > summary.MaxSignalPct) summary.MaxSignalPct = pct;
                            if (validRssi && (!summary.HasRssi || rssi > summary.MaxRssiDbm))
                            {
                                summary.MaxRssiDbm = rssi;
                                summary.HasRssi = true;
                            }

                            uint freqKhz = entry.ulChCenterFrequency;
                            if (freqKhz >= 2400000u && freqKhz <= 2500000u)
                            {
                                summary.Band24Count++;
                                if (pct > summary.Max24Pct) summary.Max24Pct = pct;
                            }
                            else if (freqKhz >= 4900000u && freqKhz <= 5900000u)
                            {
                                summary.Band5Count++;
                                if (pct > summary.Max5Pct) summary.Max5Pct = pct;
                            }
                            else if (freqKhz >= 5925000u && freqKhz <= 7125000u)
                            {
                                summary.Band6Count++;
                                if (pct > summary.Max6Pct) summary.Max6Pct = pct;
                            }

                            if (!string.IsNullOrEmpty(targetServiceSsid) &&
                                string.Equals(ssid, targetServiceSsid, StringComparison.Ordinal))
                            {
                                summary.ServiceSsidFound = true;
                                if (pct > summary.ServiceSignalPct) summary.ServiceSignalPct = pct;
                                if (validRssi && (!summary.HasServiceRssi || rssi > summary.ServiceRssiDbm))
                                {
                                    summary.ServiceRssiDbm = rssi;
                                    summary.HasServiceRssi = true;
                                }
                            }
                        }
                    }
                    finally
                    {
                        if (pBssList != IntPtr.Zero)
                        {
                            try { WlanFreeMemory(pBssList); } catch { }
                        }
                    }
                }

                summary.UniqueSsidCount = uniqueSsids.Count;
                return summary;
            }
            catch
            {
                return summary;
            }
            finally
            {
                if (pIfList != IntPtr.Zero)
                {
                    try { WlanFreeMemory(pIfList); } catch { }
                }
                if (clientHandle != IntPtr.Zero)
                {
                    try { WlanCloseHandle(clientHandle, IntPtr.Zero); } catch { }
                }
            }
        }

        public static bool TriggerActiveWlanScan()
        {
            IntPtr clientHandle = IntPtr.Zero;
            IntPtr pIfList = IntPtr.Zero;
            bool scannedAny = false;
            try
            {
                uint negotiatedVersion;
                if (WlanOpenHandle(2u, IntPtr.Zero, out negotiatedVersion, out clientHandle) != 0u || clientHandle == IntPtr.Zero)
                {
                    return false;
                }
                if (WlanEnumInterfaces(clientHandle, IntPtr.Zero, out pIfList) != 0u || pIfList == IntPtr.Zero)
                {
                    return false;
                }
                System.Collections.Generic.List<Guid> ifaces = CollectWlanInterfaceGuids(pIfList);
                for (int i = 0; i < ifaces.Count; i++)
                {
                    Guid ifaceGuid = ifaces[i];
                    if (WlanScan(clientHandle, ref ifaceGuid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0u)
                    {
                        scannedAny = true;
                    }
                }
                return scannedAny;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (pIfList != IntPtr.Zero)
                {
                    try { WlanFreeMemory(pIfList); } catch { }
                }
                if (clientHandle != IntPtr.Zero)
                {
                    try { WlanCloseHandle(clientHandle, IntPtr.Zero); } catch { }
                }
            }
        }

        /// <summary>
        /// Potiskuje Windows 11 24H2+ modalni dijalog "Your location is currently turned off / Got it"
        /// postavljanjem ShowGlobalPrompts = 0 u ConsentStore\location.
        /// </summary>
        public static void SuppressLocationPromptPopups()
        {
            const string subKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location";
            foreach (Microsoft.Win32.RegistryKey root in new Microsoft.Win32.RegistryKey[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            {
                try
                {
                    using (Microsoft.Win32.RegistryKey key = root.CreateSubKey(subKey))
                    {
                        if (key != null)
                        {
                            key.SetValue("ShowGlobalPrompts", 0, Microsoft.Win32.RegistryValueKind.DWord);
                        }
                    }
                }
                catch { }
            }
        }

        private static bool IsConsentLocationDenied(Microsoft.Win32.RegistryKey root, string subKey)
        {
            if (root == null || string.IsNullOrEmpty(subKey)) return false;
            try
            {
                using (Microsoft.Win32.RegistryKey consent = root.OpenSubKey(subKey, false))
                {
                    if (consent != null)
                    {
                        object v = consent.GetValue("Value");
                        if (v != null && string.Equals(v.ToString().Trim(), "Deny", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        public static bool IsLocationPolicyDisabled()
        {
            const string consentSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location";
            const string consentNonPackagedSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location\NonPackaged";
            try
            {
                Microsoft.Win32.RegistryView regView = Environment.Is64BitOperatingSystem
                    ? Microsoft.Win32.RegistryView.Registry64
                    : Microsoft.Win32.RegistryView.Default;

                using (Microsoft.Win32.RegistryKey hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    Microsoft.Win32.RegistryHive.LocalMachine, regView))
                {
                    using (Microsoft.Win32.RegistryKey locPol = hklm.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", false))
                    {
                        if (locPol != null)
                        {
                            object v = locPol.GetValue("DisableLocation");
                            if (v != null)
                            {
                                try { if (Convert.ToInt32(v) == 1) return true; }
                                catch { }
                            }
                            object vp = locPol.GetValue("DisableWindowsLocationProvider");
                            if (vp != null)
                            {
                                try { if (Convert.ToInt32(vp) == 1) return true; }
                                catch { }
                            }
                        }
                    }

                    using (Microsoft.Win32.RegistryKey appPrivacy = hklm.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", false))
                    {
                        if (appPrivacy != null)
                        {
                            object v = appPrivacy.GetValue("LetAppsAccessLocation");
                            if (v != null)
                            {
                                try { if (Convert.ToInt32(v) == 2) return true; }
                                catch { }
                            }
                        }
                    }

                    if (IsConsentLocationDenied(hklm, consentSubKey) ||
                        IsConsentLocationDenied(hklm, consentNonPackagedSubKey))
                    {
                        return true;
                    }
                }

                using (Microsoft.Win32.RegistryKey hkcu = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    Microsoft.Win32.RegistryHive.CurrentUser, regView))
                {
                    if (IsConsentLocationDenied(hkcu, @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location") ||
                        IsConsentLocationDenied(hkcu, @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location\NonPackaged"))
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static WlanCurrentConnectionInfo QueryCurrentWifiConnection()
        {
            WlanCurrentConnectionInfo best = new WlanCurrentConnectionInfo();
            IntPtr clientHandle = IntPtr.Zero;
            IntPtr pIfList = IntPtr.Zero;
            try
            {
                uint negotiatedVersion;
                uint openErr = WlanOpenHandle(2u, IntPtr.Zero, out negotiatedVersion, out clientHandle);
                if (openErr != 0u || clientHandle == IntPtr.Zero)
                {
                    best.LastError = openErr;
                    return best;
                }

                uint enumErr = WlanEnumInterfaces(clientHandle, IntPtr.Zero, out pIfList);
                if (enumErr != 0u || pIfList == IntPtr.Zero)
                {
                    best.LastError = enumErr;
                    return best;
                }

                System.Collections.Generic.List<Guid> ifaces = CollectWlanInterfaceGuids(pIfList);
                uint minStructSize = (uint)Marshal.SizeOf(typeof(WLAN_CONNECTION_ATTRIBUTES));

                for (int i = 0; i < ifaces.Count; i++)
                {
                    Guid ifaceGuid = ifaces[i];
                    IntPtr pConnData = IntPtr.Zero;
                    IntPtr pChanData = IntPtr.Zero;
                    try
                    {
                        uint dataSize;
                        uint valType;
                        uint err = WlanQueryInterface(
                            clientHandle,
                            ref ifaceGuid,
                            wlan_intf_opcode_current_connection,
                            IntPtr.Zero,
                            out dataSize,
                            out pConnData,
                            out valType);

                        if (err != 0u || pConnData == IntPtr.Zero || dataSize < minStructSize)
                        {
                            if (err != 0u) best.LastError = err;
                            continue;
                        }

                        WLAN_CONNECTION_ATTRIBUTES attr = (WLAN_CONNECTION_ATTRIBUTES)Marshal.PtrToStructure(pConnData, typeof(WLAN_CONNECTION_ATTRIBUTES));
                        if (attr.isState != WLAN_INTERFACE_STATE_CONNECTED) continue;

                        string ssid = "";
                        int ssidLen = (int)Math.Min(attr.wlanAssociationAttributes.dot11Ssid.uSSIDLength, 32u);
                        if (ssidLen > 0 && attr.wlanAssociationAttributes.dot11Ssid.ucSSID != null)
                        {
                            int safeLen = Math.Min(ssidLen, attr.wlanAssociationAttributes.dot11Ssid.ucSSID.Length);
                            ssid = System.Text.Encoding.UTF8.GetString(attr.wlanAssociationAttributes.dot11Ssid.ucSSID, 0, safeLen).TrimEnd('\0');
                        }

                        int sigPct = (int)Math.Min(100u, attr.wlanAssociationAttributes.wlanSignalQuality);
                        uint chNum = 0u;
                        try
                        {
                            uint chSize;
                            uint chType;
                            if (WlanQueryInterface(
                                clientHandle,
                                ref ifaceGuid,
                                wlan_intf_opcode_channel_number,
                                IntPtr.Zero,
                                out chSize,
                                out pChanData,
                                out chType) == 0u && pChanData != IntPtr.Zero && chSize >= 4u)
                            {
                                chNum = unchecked((uint)Marshal.ReadInt32(pChanData, 0));
                            }
                        }
                        catch { }

                        if (!best.Connected || sigPct > best.SignalPct)
                        {
                            best.Connected = true;
                            best.Ssid = ssid;
                            best.SignalPct = sigPct;
                            best.PhyType = attr.wlanAssociationAttributes.dot11PhyType;
                            best.ChannelNumber = chNum;
                            best.RxRateKbps = attr.wlanAssociationAttributes.ulRxRate;
                            best.TxRateKbps = attr.wlanAssociationAttributes.ulTxRate;
                            best.LastError = 0u;
                        }
                    }
                    finally
                    {
                        if (pChanData != IntPtr.Zero)
                        {
                            try { WlanFreeMemory(pChanData); } catch { }
                        }
                        if (pConnData != IntPtr.Zero)
                        {
                            try { WlanFreeMemory(pConnData); } catch { }
                        }
                    }
                }
                return best;
            }
            catch
            {
                return best;
            }
            finally
            {
                if (pIfList != IntPtr.Zero)
                {
                    try { WlanFreeMemory(pIfList); } catch { }
                }
                if (clientHandle != IntPtr.Zero)
                {
                    try { WlanCloseHandle(clientHandle, IntPtr.Zero); } catch { }
                }
            }
        }
        #endregion
    }
}
