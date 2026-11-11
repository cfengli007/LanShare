namespace LanShare.Desktop.Services;

/// <summary>通过管理员 netsh 放行当前分享端口的入站 TCP 连接（专用/域网络）。</summary>
public static class FirewallHelper
{
    public static Task<ElevationResult> AllowPortAsync(int port)
    {
        var ruleName = $"LanShare (TCP {port})";
        var script = $@"
netsh advfirewall firewall delete rule name=""{ruleName}"" | Out-Null
netsh advfirewall firewall add rule name=""{ruleName}"" dir=in action=allow protocol=TCP localport={port} profile=private,domain | Out-Null
";
        return ElevationHelper.RunElevatedAsync("防火墙放行", script);
    }

    public static Task<ElevationResult> RemoveRulesAsync()
    {
        var script = @"
netsh advfirewall firewall delete rule name=""LanShare"" | Out-Null
Get-NetFirewallRule -DisplayName 'LanShare (TCP *)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
";
        return ElevationHelper.RunElevatedAsync("移除防火墙规则", script);
    }
}
