using SpaceTestPC.App.Models;
using SpaceTestPC.App.Services;

var portName = args.Length > 0 ? args[0] : "COM5";
var batteryVoltageMv = args.Length > 1 && int.TryParse(args[1], out var parsedVoltageMv) ? parsedVoltageMv : 7400;
var sampleCount = args.Length > 2 && int.TryParse(args[2], out var parsedSampleCount) ? parsedSampleCount : 10;
var sampleIntervalMs = args.Length > 3 && int.TryParse(args[3], out var parsedSampleIntervalMs) ? parsedSampleIntervalMs : 500;
var keepOutputOn = args.Length > 4 ? !string.Equals(args[4], "off", StringComparison.OrdinalIgnoreCase) : true;

var service = new Jk5506Service(new Jk5506Configuration
{
    Enabled = true,
    PortName = portName,
    BaudRate = 115200,
    SlaveAddress = 1,
    TimeoutMs = 1000
});

Console.WriteLine($"JK5506 verifier started. port={portName}, batteryVoltageMv={batteryVoltageMv}, sampleCount={sampleCount}, sampleIntervalMs={sampleIntervalMs}, keepOutputOn={keepOutputOn}");
await service.PrepareChargeTestAsync(batteryVoltageMv);
Console.WriteLine("JK5506 output enabled.");

try
{
    for (var index = 1; index <= sampleCount; index++)
    {
        var voltageMv = await service.ReadOutputVoltageMvAsync();
        var currentMa = await service.ReadOutputCurrentMaAsync();
        Console.WriteLine($"sample={index}/{sampleCount}, voltage={voltageMv}mV, current={currentMa}mA");
        if (index < sampleCount)
        {
            await Task.Delay(sampleIntervalMs);
        }
    }
}
finally
{
    if (keepOutputOn)
    {
        Console.WriteLine("JK5506 output left enabled.");
    }
    else
    {
        await service.StopOutputAsync();
        Console.WriteLine("JK5506 output disabled.");
    }
}
