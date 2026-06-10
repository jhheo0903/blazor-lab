using Microsoft.AspNetCore.SignalR;

namespace DeployTool.Hubs;

public class DeployLogHub : Hub
{
    public async Task SendLog(string message, bool isError, bool isWarning)
    {
        await Clients.All.SendAsync("ReceiveLog", message, isError, isWarning);
    }
}
