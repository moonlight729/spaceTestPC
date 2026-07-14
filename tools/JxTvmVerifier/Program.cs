using SpaceTestPC.App.Models;
using SpaceTestPC.App.Services;

var service = new JxTvmService(new JxTvmConfiguration
{
    Enabled = true,
    PortName = "COM7"
});

var probe = await service.ProbeAsync();
var voltageMv = await service.ReadChannelVoltageMvAsync(1);
Console.WriteLine($"JX-TVM communication succeeded. Register 1000: {probe}, channel 1: {voltageMv}mV");
