using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Input;
using Windows.Devices.Radios;
using System.Runtime.InteropServices;

namespace BLE_EmulatorTest;

class Program
{
    private static VirtualKeyboard? m_virtualKeyboard;
    private static VirtualConsumer? m_virtualConsumer;
    private static VirtualMouse? m_virtualMouse;
    private static string VER = "9.1";
    private static BlockingCollection<string> m_cmds = new BlockingCollection<string>();
    private static IReadOnlyList<Windows.Devices.Bluetooth.GenericAttributeProfile.GattSubscribedClient>? m_subscribedClients;
    private static unsafe delegate* unmanaged<sbyte*, void> m_sendStringCallback;
    private static unsafe delegate* unmanaged<sbyte*, void> m_sendLogCallback;

    public static void LogInfo(string message) { SendLog(message); }
    public static void LogDebug(string message) { /*SendLog(message);*/ }


    private static async Task<bool> InitializeVirtualDevices()
    {
        try
        {
            m_virtualKeyboard = new VirtualKeyboard();
            m_virtualKeyboard.SubscribedHidClientsChanged += VirtualKeyboard_SubscribedHidClientsChanged;
            await m_virtualKeyboard.InitilizeAsync();
            m_virtualKeyboard.Enable();

            m_virtualConsumer = new VirtualConsumer();
            m_virtualConsumer.SubscribedHidClientsChanged += VirtualConsumer_SubscribedHidClientsChanged;
            await m_virtualConsumer.InitilizeAsync();
            m_virtualConsumer.Enable();

            m_virtualMouse = new VirtualMouse();
            m_virtualMouse.SubscribedHidClientsChanged += VirtualMouse_SubscribedHidClientsChanged;
            await m_virtualMouse.InitilizeAsync();
            m_virtualMouse.Enable();

            LogInfo("InitializeVirtualDevices - finished!");
            return true;
        }
        catch (Exception e)
        {
            LogInfo($"Error: {e.ToString()}");
            SendString("DEVICE=BLE_ERROR\n");
            return false;
        }
    }

    private static void DeviceConnectionStatusChange(BluetoothLEDevice sender, object args)
    {
        LogInfo($"DeviceConnectionStatusChange - device: {sender.Name}  status: {sender.ConnectionStatus}");
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected)
        {
            SendString($"DEVICE={sender.Name}\n");
        }
        else if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            SendString("DEVICE=NONE\n");
        }
    }

    private static async void VirtualKeyboard_SubscribedHidClientsChanged(IReadOnlyList<Windows.Devices.Bluetooth.GenericAttributeProfile.GattSubscribedClient> subscribedClients)
    {
        if (subscribedClients != null)
        {
            foreach (var client in subscribedClients)
            {
                var leDevice = await BluetoothLEDevice.FromIdAsync(client.Session.DeviceId.Id);
                LogInfo("keyboard-subscribed: " + leDevice.Name);
            }
        }
    }

    private static async void VirtualConsumer_SubscribedHidClientsChanged(IReadOnlyList<Windows.Devices.Bluetooth.GenericAttributeProfile.GattSubscribedClient> subscribedClients)
    {
        if (subscribedClients != null)
        {
            foreach (var client in subscribedClients)
            {
                var leDevice = await BluetoothLEDevice.FromIdAsync(client.Session.DeviceId.Id);
                LogInfo("consumer-subscribed: " + leDevice.Name);
            }
        }
    }

    // this one we'll use to actually track connect/disconnect
    private static async void VirtualMouse_SubscribedHidClientsChanged(IReadOnlyList<Windows.Devices.Bluetooth.GenericAttributeProfile.GattSubscribedClient> subscribedClients)
    {
        m_subscribedClients = subscribedClients;
        if (subscribedClients != null)
        {
            foreach (var client in subscribedClients)
            {
                var leDevice = await BluetoothLEDevice.FromIdAsync(client.Session.DeviceId.Id);
                leDevice.ConnectionStatusChanged += DeviceConnectionStatusChange;
                LogInfo("mouse-subscribed: " + leDevice.Name);
                SendString($"DEVICE={leDevice.Name}\n");
            }
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "AddCmd")]
    public static unsafe void AddCmd(sbyte* _cmd)
    {
        string cmd = Marshal.PtrToStringUTF8((IntPtr)_cmd);
        m_cmds.Add(cmd);
        LogDebug($"AddCmd: \"{cmd}\"");
    }

    private static unsafe void SendString(string str)
    {
        if (m_sendStringCallback is not null)
        {
            byte[] utf8Bytes = Encoding.UTF8.GetBytes(str + "\0");
            IntPtr hGlobal = Marshal.AllocHGlobal(utf8Bytes.Length);
            Marshal.Copy(utf8Bytes, 0, hGlobal, utf8Bytes.Length);
            try
            {
                m_sendStringCallback((sbyte*)hGlobal);
            }
            finally
            {
                Marshal.FreeHGlobal(hGlobal);
            }
        }
    }

    private static unsafe void SendLog(string str)
    {
        if (m_sendLogCallback is not null)
        {
            byte[] utf8Bytes = Encoding.UTF8.GetBytes(str + "\0");
            IntPtr hGlobal = Marshal.AllocHGlobal(utf8Bytes.Length);
            Marshal.Copy(utf8Bytes, 0, hGlobal, utf8Bytes.Length);
            try
            {
                m_sendLogCallback((sbyte*)hGlobal);
            }
            finally
            {
                Marshal.FreeHGlobal(hGlobal);
            }
        }
    }

    private static async Task run_server()
    {
        if (!await GetBluetoothIsEnabled() || !await InitializeVirtualDevices())
        {
            SendString("DEVICE=BLE_ERROR\n");
            LogInfo("exiting run_server");
            return;
        }

        while (true)
        {
            LogDebug("checking cmd queue...");

            /*
            string? str;
            var found = m_cmds.TryTake(out str, 1000);
            if (!found)
                continue;
            */
            var str = m_cmds.Take();
            str = str.Replace("\n", "").Replace("\r", "").TrimEnd('\0').ToLower();
            LogDebug("got cmd: " + str);
            SendString("\x06\n"); // ACK when we get to actually processing it

            if (str.StartsWith("ping"))
            {
                SendString("pong\n");
            }
            else if (str.StartsWith("mv"))
            {
                var args = str.Split(new char[] { '=' })[1].Split(',');
                await m_virtualMouse.Move(int.Parse(args[0]), int.Parse(args[1]), 0);
                SendString("OK\n");
            }
            else if (str.StartsWith("mw"))
            {
                var args = str.Split(new char[] { '=' })[1].Split(',');
                await m_virtualMouse.Move(0, 0, int.Parse(args[0]));
                SendString("OK\n");
            }
            else if (str.StartsWith("mb"))
            {
                var args = str.Split(new char[] { '=' })[1].Split(',');
                if (args[0] == "l")
                {
                    if (args[1] == "1")
                    {
                        await m_virtualMouse.Press();
                    }
                    else if (args[1] == "0")
                    {
                        await m_virtualMouse.Release();
                    }
                }
                SendString("OK\n");
            }
            else if (str.StartsWith("kb"))
            {
                var args = str.Split(new char[] { '=' })[1].Split('-');
                var reportValue = new byte[VirtualKeyboard.c_sizeOfReportDataInBytes];
                for (int i = 0; i < args.Length; i++)
                {
                    reportValue[i] = byte.Parse(args[i], NumberStyles.HexNumber);
                }
                await m_virtualKeyboard.DirectSendReport(reportValue);
                SendString("OK\n");
            }
            else if (str.StartsWith("cr"))
            {
                var arg = str.Split(new char[] { '=' })[1];
                var val = byte.Parse(arg, NumberStyles.HexNumber);
                var reportValue = new byte[VirtualConsumer.c_sizeOfReportDataInBytes];

                reportValue[0] = (byte)(val & 0xFF);
                reportValue[1] = (byte)(val >> 8);

                LogDebug($"ConsumerReport: {reportValue[0]:X},{reportValue[1]:X}");

                await m_virtualConsumer.DirectSendReport(reportValue);
                SendString("OK\n");
            }
            else
            {
                LogDebug("UNKNOWN CMD!!!");
                SendString("ERROR=unknown cmd\n");
            }
        }
    }

    private static void BluetoothRadio_StateChanged(Radio sender, object args)
    {
        if (sender.State != RadioState.On)
        {
            LogInfo("Bluetooth turned OFF");
            SendString("DEVICE=BLE_ERROR\n");
        }
    }

    static async Task<bool> GetBluetoothIsEnabled()
    {
        var radios = await Radio.GetRadiosAsync();
        var bluetoothRadio = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);

        if (bluetoothRadio != null)
            bluetoothRadio.StateChanged += BluetoothRadio_StateChanged;

        var retval = bluetoothRadio != null && bluetoothRadio.State == RadioState.On;
        LogInfo("GetBluetoothIsEnabled - " + retval);
        return retval;
    }

    [UnmanagedCallersOnly(EntryPoint = "Start")]
    public static unsafe void Start(delegate* unmanaged<sbyte*, void> sendLogCallback, delegate* unmanaged<sbyte*, void> sendStringCallback)
    {
        m_sendLogCallback = sendLogCallback;
        m_sendStringCallback = sendStringCallback;

        SendString("VER=" + VER + "\n");
        SendString("DEVICE=NONE\n");
        run_server().GetAwaiter().GetResult();
        LogInfo("Start - finished!");
    }
}
