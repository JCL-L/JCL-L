using System;
using System.Runtime.InteropServices;

namespace NinOne.Drivers.ZlgCan
{
    internal static class ZlgNative
    {
        internal const uint UsbCanFd200U = 41;
        internal const uint UsbCanFd100U = 42;
        internal const uint CanTypeClassic = 0;
        internal const uint CanTypeFd = 1;
        internal const uint StatusOk = 1;
        internal const uint StatusOnline = 2;
        internal const uint StatusError = 0;
        internal const byte CanFdBitRateSwitch = 0x01;
        internal const byte CanFdErrorStateIndicator = 0x02;
        internal const uint ExtendedFlag = 0x80000000;
        internal const uint RemoteFlag = 0x40000000;
        internal const uint IdMask = 0x1FFFFFFF;

        [StructLayout(LayoutKind.Explicit, Size = 32)]
        internal struct ChannelInitConfig
        {
            [FieldOffset(0)] public uint CanType;
            [FieldOffset(4)] public uint AcceptanceCode;
            [FieldOffset(8)] public uint AcceptanceMask;
            [FieldOffset(12)] public uint Reserved;
            [FieldOffset(16)] public byte Filter;
            [FieldOffset(17)] public byte Timing0;
            [FieldOffset(18)] public byte Timing1;
            [FieldOffset(19)] public byte Mode;
            [FieldOffset(12)] public uint FdArbitrationTiming;
            [FieldOffset(16)] public uint FdDataTiming;
            [FieldOffset(20)] public uint FdBrp;
            [FieldOffset(24)] public byte FdFilter;
            [FieldOffset(25)] public byte FdMode;
            [FieldOffset(26)] public ushort FdPadding;
            [FieldOffset(28)] public uint FdReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeCanFrame
        {
            public uint CanId;
            public byte CanDlc;
            public byte Pad;
            public byte Reserved0;
            public byte Reserved1;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TransmitData
        {
            public NativeCanFrame Frame;
            public uint TransmitType;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ReceiveData
        {
            public NativeCanFrame Frame;
            public ulong Timestamp;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeCanFdFrame
        {
            public uint CanId;
            public byte Length;
            public byte Flags;
            public byte Reserved0;
            public byte Reserved1;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TransmitFdData
        {
            public NativeCanFdFrame Frame;
            public uint TransmitType;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ReceiveFdData
        {
            public NativeCanFdFrame Frame;
            public ulong Timestamp;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ChannelErrorInfo
        {
            public uint ErrorCode;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public byte[] PassiveErrorData;
            public byte ArbitrationLostData;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ChannelStatus
        {
            public byte ErrorInterrupt;
            public byte Mode;
            public byte Status;
            public byte ArbitrationLostCapture;
            public byte ErrorCodeCapture;
            public byte ErrorWarningLimit;
            public byte ReceiveErrorCounter;
            public byte TransmitErrorCounter;
            public uint Reserved;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetDllDirectory(string pathName);

        [DllImport("zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern IntPtr ZCAN_OpenDevice(uint deviceType, uint deviceIndex, uint reserved);

        [DllImport("zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint ZCAN_CloseDevice(IntPtr deviceHandle);

        [DllImport("zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint ZCAN_IsDeviceOnLine(IntPtr deviceHandle);

        [DllImport("zlgcan.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern uint ZCAN_SetValue(
            IntPtr deviceHandle,
            [MarshalAs(UnmanagedType.LPStr)] string path,
            [MarshalAs(UnmanagedType.LPStr)] string value);

        [DllImport("zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern IntPtr ZCAN_InitCAN(IntPtr deviceHandle, uint channelIndex, ref ChannelInitConfig config);

        [DllImport("zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint ZCAN_StartCAN(IntPtr channelHandle);

        [DllImport("zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint ZCAN_ResetCAN(IntPtr channelHandle);

        [DllImport("zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint ZCAN_ClearBuffer(IntPtr channelHandle);

        [DllImport("zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint ZCAN_ReadChannelErrInfo(IntPtr channelHandle, out ChannelErrorInfo errorInfo);

        [DllImport("zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint ZCAN_ReadChannelStatus(IntPtr channelHandle, out ChannelStatus status);

        [DllImport("zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint ZCAN_Transmit(IntPtr channelHandle, ref TransmitData transmit, uint length);

        [DllImport("zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint ZCAN_Receive(IntPtr channelHandle, [In, Out] ReceiveData[] receive, uint length, int waitMilliseconds);

        [DllImport("zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint ZCAN_TransmitFD(IntPtr channelHandle, ref TransmitFdData transmit, uint length);

        [DllImport("zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint ZCAN_ReceiveFD(IntPtr channelHandle, [In, Out] ReceiveFdData[] receive, uint length, int waitMilliseconds);
    }
}
