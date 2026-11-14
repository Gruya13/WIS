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

        public const uint RIDEV_INPUTSINK = 0x00000100;
        public const uint RID_INPUT = 0x10000003;
        public const int RIM_TYPEKEYBOARD = 1;
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
        private const uint CM_DRP_LOCATION_INFORMATION = 0x0000000E;
        private const uint CM_DRP_ADDRESS = 0x0000001D;
        private const uint CM_DRP_LOCATION_PATHS = 0x00000024;

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

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

        /// <summary>
        /// Pronalazi jedinstvenu fizicku putanju USB porta penjanjem kroz PnP stablo do USB\... parent cvora.
        /// Spasava situaciju kada USBSTOR\DISK nema LocationPaths ili je prazan u WMI.
        /// </summary>
        public static string ResolveUsbPhysicalPortLocation(string pnpDeviceId, out string debugInfo)
        {
            debugInfo = "";
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

                debugInfo = string.Format(
                    "leafPaths='{0}' leafInfo='{1}' usbNode='{2}' usbPaths='{3}' usbInfo='{4}' addr={5} hub='{6}'",
                    leafLocPaths ?? "", leafLocInfo ?? "", usbNodeId ?? "", usbLocPaths ?? "", usbLocInfo ?? "", usbAddr, hubNodeId ?? "");

                if (!string.IsNullOrEmpty(usbLocPaths))
                {
                    if (!string.IsNullOrEmpty(usbLocInfo))
                        return usbLocPaths + " (" + usbLocInfo + ")";
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
    }
}
