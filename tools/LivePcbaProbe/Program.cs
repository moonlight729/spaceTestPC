using SpaceTestPC.App.Models;
using SpaceTestPC.App.Services;

var sessionId = $"live-probe-{DateTimeOffset.Now:yyyyMMddHHmmss}";
var sn = "LIVE-PCBA-001";
var client = new AdbPcbaCommandClient(deviceSerial: "66a690f21049a8d7");

Console.WriteLine($"Session: {sessionId}");
var state = await client.GetBoardStateAsync(sessionId, sn);
Console.WriteLine($"BoardState: boardId={state.BoardId}, boardSn={state.BoardSn}, totalCount={state.TotalCount}");

var plan = new[]
{
    new TestPlanItem
    {
        Id = "battery_management",
        Parameters = new Dictionary<string, object?>
        {
            ["timeoutMs"] = 15000,
            ["dischargeCurrentMinMa"] = 450,
            ["dischargeCurrentMaxMa"] = 550
        }
    }
};

await foreach (var testEvent in client.RunSessionAsync(sessionId, sn, plan))
{
    Console.WriteLine($"{testEvent.Event} | {testEvent.TestId} | {testEvent.Status} | {testEvent.ResultCode} | {testEvent.Message}");
    if (testEvent.Data.Count > 0)
    {
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(testEvent.Data));
    }

    if (testEvent.TestId == "battery_management" &&
        testEvent.Status == "running" &&
        testEvent.Data.TryGetValue("readyForHostDecision", out var ready) &&
        ready?.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
    {
        await client.SubmitTestDecisionAsync(sessionId, testEvent.TestId, true, "live_pc_host_pass");
        Console.WriteLine("Submitted host decision: PASS");
    }
}
