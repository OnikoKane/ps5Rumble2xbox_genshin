// XInputProbe — small P/Invoke probe of the classic XInput API, to demonstrate
// in the same run that XInput only exposes TWO motor speeds (left/right grip)
// and to report which XInput slots are actually occupied.
//
// XInput has no notion of trigger impulse motors: XINPUT_VIBRATION has exactly
// wLeftMotorSpeed / wRightMotorSpeed. This is the API comparison evidence.

using System;
using System.Runtime.InteropServices;

namespace Xbox4Motor;

internal static class XInputProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_VIBRATION
    {
        public ushort wLeftMotorSpeed;
        public ushort wRightMotorSpeed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
    private static extern uint XInputSetState(uint dwUserIndex, ref XINPUT_VIBRATION pVibration);

    private const uint ERROR_SUCCESS = 0;
    private const uint ERROR_DEVICE_NOT_CONNECTED = 1167;

    public static void PrintSlots()
    {
        Console.WriteLine("\nXInput 槽位探测（xinput1_4.dll）：");
        int connected = 0;
        for (uint i = 0; i < 4; i++)
        {
            uint r = XInputGetState(i, out var st);
            if (r == ERROR_SUCCESS)
            {
                connected++;
                Console.WriteLine($"  [slot {i}] 已连接  buttons=0x{st.Gamepad.wButtons:X4}  LT={st.Gamepad.bLeftTrigger}  RT={st.Gamepad.bRightTrigger}  LX={st.Gamepad.sThumbLX} LY={st.Gamepad.sThumbLY}");
            }
            else if (r == ERROR_DEVICE_NOT_CONNECTED)
            {
                Console.WriteLine($"  [slot {i}] 未连接");
            }
            else
            {
                Console.WriteLine($"  [slot {i}] 错误 0x{r:X}（可能缺少 xinput1_4.dll）");
            }
        }
        Console.WriteLine($"  => XInput 共 {connected} 个已连接。注意：XINPUT_VIBRATION 只有 wLeftMotorSpeed/wRightMotorSpeed 两个马达值（无扳机脉冲马达）。");
    }
}
