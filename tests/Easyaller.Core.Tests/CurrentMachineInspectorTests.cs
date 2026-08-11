using Easyaller.App;

namespace Easyaller.Core.Tests;

public sealed class CurrentMachineInspectorTests
{
    [Fact]
    public void Parse_FullConfiguration_ReadsEveryField()
    {
        const string output = """
            {"computerName":"SITE01NOMAD69","domain":"corp.example","timeZone":"UTC","adapterId":"Ethernet","address":"192.0.2.77","prefixLength":24,"defaultGateway":"192.0.2.254","dnsServers":["192.0.2.53","198.51.100.53"],"proxyRaw":""}
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
    public void Parse_EnglishProxyOutput_ReadsAddressAndBypassList()
    {
        const string output = """
            {"proxyRaw":"Current WinHTTP proxy settings:\r\n\r\n    Proxy Server(s) :  proxy.example.test:8080\r\n    Bypass List     :  *.example.test;<local>\r\n"}
            """;

        var snapshot = CurrentMachineInspector.Parse(output);

        Assert.Equal("proxy.example.test:8080", snapshot!.ProxyAddress);
        Assert.Equal("*.example.test;<local>", snapshot.ProxyBypassList);
    }

    [Fact]
    public void Parse_LocalizedProxyOutput_StillReadsAddress()
    {
        // The address is matched by shape because netsh labels are translated.
        const string output = """
            {"proxyRaw":"Текущие параметры прокси WinHTTP:\r\n\r\n    Прокси-сервер:  10.10.0.5:3128\r\n"}
            """;

        var snapshot = CurrentMachineInspector.Parse(output);

        Assert.Equal("10.10.0.5:3128", snapshot!.ProxyAddress);
    }

    [Fact]
    public void Parse_DirectAccessProxyOutput_ReportsNoProxy()
    {
        const string output = """
            {"proxyRaw":"Current WinHTTP proxy settings:\r\n\r\n    Direct access (no proxy server).\r\n"}
            """;

        var snapshot = CurrentMachineInspector.Parse(output);

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
            """{"computerName":"PC01","domain":"corp.example","timeZone":"UTC","adapterId":"Ethernet","address":"192.0.2.77","prefixLength":24,"defaultGateway":"192.0.2.254","dnsServers":["192.0.2.53"],"proxyRaw":"Proxy Server(s) : proxy.example.test:8080"}""");

        var machine = snapshot!.ToMachineState();

        Assert.Equal("PC01", machine.ComputerName);
        Assert.Equal("corp.example", machine.Domain);
        Assert.Equal("UTC", machine.TimeZone);
        Assert.Equal("255.255.255.0", machine.SubnetMask);
        Assert.Equal("proxy.example.test:8080", machine.ProxyAddress);
    }
}
