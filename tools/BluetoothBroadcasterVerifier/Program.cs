using SpaceTestPC.Core.Models;
using SpaceTestPC.Core.Services;

var service = new BluetoothBroadcasterService(new BluetoothBroadcasterConfiguration
{
    Enabled = true,
    PortName = "COM6",
    BroadcastName = "yctc_bt_01"
});

await service.ConfigureAsync();
Console.WriteLine("Bluetooth broadcaster configured successfully on COM6: yctc_bt_01");
var status = await service.ReadStatusAsync();
foreach (var item in status)
{
    Console.WriteLine($"{item.Key}: {item.Value}");
}
