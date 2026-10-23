using System;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace PhoneMic
{
    /// <summary>
    /// 把某个录音设备设成系统默认（含「默认设备」和「默认通信设备」两个角色）。
    ///
    /// Windows 没有公开的 API 做这件事，只能用未公开的 IPolicyConfig 接口。
    /// 这是业界通行做法（音量控制类软件都这么干）。
    /// 只需要 SetDefaultEndpoint 这一个方法，其余方法只是为了占住 vtable 位置。
    /// </summary>
    internal static class AudioDefault
    {
        [ComImport]
        [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
        private class CPolicyConfigClient
        {
        }

        [ComImport]
        [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            // 下面这些方法我们都不调用，纯粹为了保持 vtable 顺序与系统一致。
            // 顺序错一个，SetDefaultEndpoint 就会调到别的方法上去。
            [PreserveSig] int GetMixFormat(string pszDeviceName, out IntPtr ppFormat);
            [PreserveSig] int GetDeviceFormat(string pszDeviceName, bool bDefault, out IntPtr ppFormat);
            [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
            [PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod(string pszDeviceName, bool bDefault, out long pmftDefaultPeriod, out long pmftMinimumPeriod);
            [PreserveSig] int SetProcessingPeriod(string pszDeviceName, ref long pmftPeriod);
            [PreserveSig] int GetShareMode(string pszDeviceName, out int pMode);
            [PreserveSig] int SetShareMode(string pszDeviceName, ref int pMode);
            [PreserveSig] int GetPropertyValue(string pszDeviceName, bool bFxStore, ref IntPtr key, out IntPtr pv);
            [PreserveSig] int SetPropertyValue(string pszDeviceName, bool bFxStore, ref IntPtr key, ref IntPtr pv);
            [PreserveSig] int SetDefaultEndpoint(string pszDeviceName, int role);
            [PreserveSig] int SetEndpointVisibility(string pszDeviceName, bool bVisible);
        }

        /// <summary>取某个角色的默认录音设备 ID（取不到返回空串）</summary>
        public static string GetDefaultCaptureId(Role role)
        {
            try
            {
                using (var en = new MMDeviceEnumerator())
                {
                    var d = en.GetDefaultAudioEndpoint(DataFlow.Capture, role);
                    return d != null ? d.ID : "";
                }
            }
            catch { return ""; }
        }

        /// <summary>取某个角色的默认播放（扬声器）设备 ID</summary>
        public static string GetDefaultRenderId(Role role)
        {
            try
            {
                using (var en = new MMDeviceEnumerator())
                {
                    var d = en.GetDefaultAudioEndpoint(DataFlow.Render, role);
                    return d != null ? d.ID : "";
                }
            }
            catch { return ""; }
        }

        /// <summary>
        /// 把指定录音设备设成默认。role 传 Console 只影响普通程序，
        /// 传 Communications 才影响 QQ / 微信语音 / 会议 / 语音输入这类程序。
        /// </summary>
        public static void SetDefault(string deviceId, Role role)
        {
            if (string.IsNullOrEmpty(deviceId)) throw new ArgumentNullException("deviceId");

            IPolicyConfig pc = null;
            try
            {
                pc = (IPolicyConfig)new CPolicyConfigClient();
                int hr = pc.SetDefaultEndpoint(deviceId, (int)role);
                if (hr != 0)
                {
                    throw new COMException(
                        "设置默认录音设备失败（HRESULT 0x" + hr.ToString("X8") + "）", hr);
                }
            }
            finally
            {
                if (pc != null)
                {
                    try { Marshal.ReleaseComObject(pc); } catch { }
                }
            }
        }

        /// <summary>一次把「默认设备」和「默认通信设备」都设成它</summary>
        public static void SetDefaultAll(string deviceId)
        {
            SetDefault(deviceId, Role.Console);
            SetDefault(deviceId, Role.Multimedia);
            SetDefault(deviceId, Role.Communications);
        }
    }
}
