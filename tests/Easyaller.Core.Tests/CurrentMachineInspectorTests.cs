using Easyaller.App;

namespace Easyaller.Core.Tests;

public sealed class CurrentMachineInspectorTests
{
    [Fact]
    public void Parse_FullConfiguration_ReadsEveryField()
    {
        const string output = """
            {"computerName":"SITE01NOMAD69","domain":"corp.example","timeZone":"UTC","adapterId":"Ethernet","address":"192.0.2.77","prefixLength":24,"defaultGateway":"192.0.2.254","dnsServers":["192.0.2.53","198.51.100.53"],"proxyAddress":"","proxyBypassList":""}
            """;

        var snapshot = CurrentMachineInspector.Parse(output);

        Assert.NotNull(snapshot);
        Assert.Equal("SITE01NOMAD69", snapshot.ComputerName);
        Assert.Equal("corp.example", snapshot.Domain);
        Assert.Equal("UTC", snapshot.TimeZone);
        Assert.Equal("Ethernet", snapshot.AdapterId);
        Assert.Equal("192.0.2.77", snapshot.Address);
        Assert.Equal("255.255.255.0", snapshot.SubnetMask);
        Assert.Equal("192.0.2.254", snapshot.DefaultGateway);
        Assert.Equal(["192.0.2.53", "198.51.100.53"], snapshot.DnsServers);
        Assert.True(snapshot.HasNetwork);
    }

    [Theory]
    [InlineData(24, "255.255.255.0")]
    [InlineData(16, "255.255.0.0")]
    [InlineData(8, "255.0.0.0")]
    [InlineData(25, "255.255.255.128")]
    [InlineData(32, "255.255.255.255")]
    public void Parse_PrefixLength_BecomesSubnetMask(int prefixLength, string expectedMask)
    {
        var snapshot = CurrentMachineInspector.Parse(
            $$"""{"address":"192.0.2.77","prefixLength":{{prefixLength}}}""");

        Assert.Equal(expectedMask, snapshot!.SubnetMask);
    }

    [Fact]
    public void Parse_MissingPrefixLength_LeavesMaskEmpty()
    {
        var snapshot = CurrentMachineInspector.Parse("""{"address":"192.0.2.77","prefixLength":0}""");

        Assert.Equal(string.Empty, snapshot!.SubnetMask);
        Assert.False(snapshot.HasNetwork);
    }

    [Fact]
    public void Parse_SingleDnsServer_IsNotCollapsedIntoNothing()
    {
        // ConvertTo-Json writes a one-element array as a bare value.
        var snapshot = CurrentMachineInspector.Parse("""{"dnsServers":"192.0.2.53"}""");

        Assert.Equal(["192.0.2.53"], snapshot!.DnsServers);
    }

    [Fact]
    public void Parse_NoDnsServers_ReturnsEmptyList()
    {
        var snapshot = CurrentMachineInspector.Parse("""{"dnsServers":[]}""");

        Assert.Empty(snapshot!.DnsServers);
    }

    [Fact]
    public void Parse_ProxyFields_ReadsAddressAndBypassList()
    {
        // These come straight from the WinINET registry values (the proxy shown in
        // Параметры > Сеть > Прокси), so no text parsing is involved.
        const string output = """
            {"proxyAddress":"proxy.example.test:8080","proxyBypassList":"*.example.test;<local>"}
            """;

        var snapshot = CurrentMachineInspector.Parse(output);

        Assert.Equal("proxy.example.test:8080", snapshot!.ProxyAddress);
        Assert.Equal("*.example.test;<local>", snapshot.ProxyBypassList);
    }

    [Fact]
    public void Parse_NoProxyConfigured_ReportsEmptyProxy()
    {
        var snapshot = CurrentMachineInspector.Parse("""{"proxyAddress":"","proxyBypassList":""}""");

        Assert.Equal(string.Empty, snapshot!.ProxyAddress);
        Assert.Equal(string.Empty, snapshot.ProxyBypassList);
    }

    [Fact]
    public void Parse_MissingProxyFields_ReportsEmptyProxy()
    {
        var snapshot = CurrentMachineInspector.Parse("""{"computerName":"PC01"}""");

        Assert.Equal(string.Empty, snapshot!.ProxyAddress);
        Assert.Equal(string.Empty, snapshot.ProxyBypassList);
    }

    [Fact]
    public void Parse_EmptyOrMalformedOutput_ReturnsNull()
    {
        Assert.Null(CurrentMachineInspector.Parse(string.Empty));
        Assert.Null(CurrentMachineInspector.Parse("   "));
        Assert.Null(CurrentMachineInspector.Parse("not json"));
    }

    [Fact]
    public void ToMachineState_CarriesValuesIntoTheComplianceContract()
    {
        var snapshot = CurrentMachineInspector.Parse(
            """{"computerName":"PC01","domain":"corp.example","timeZone":"UTC","adapterId":"Ethernet","address":"192.0.2.77","prefixLength":24,"defaultGateway":"192.0.2.254","dnsServers":["192.0.2.53"],"proxyAddress":"proxy.example.test:8080","proxyBypassList":""}""");

        var machine = snapshot!.ToMachineState();

        Assert.Equal("PC01", machine.ComputerName);
        Assert.Equal("corp.example", machine.Domain);
        Assert.Equal("UTC", machine.TimeZone);
        Assert.Equal("255.255.255.0", machine.SubnetMask);
        Assert.Equal("proxy.example.test:8080", machine.ProxyAddress);
    }
}
