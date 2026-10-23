using MiniMouseHub.Core;

namespace MiniMouseHub.Tests;

public class DeviceNamesTests
{
    [Theory]
    [InlineData("Logitech", "PRO X 2", "Logitech PRO X Superlight 2")]
    [InlineData("Logitech", "G502 X", "Logitech G502 X")]
    [InlineData("Glorious", "GMMK Pro", "Glorious GMMK Pro")]
    [InlineData("Glorious", "Glorious GMMK Pro", "Glorious GMMK Pro")]
    public void ShowsBrandAndModel(string vendor, string name, string expected) =>
        Assert.Equal(expected, DeviceNames.Display(vendor, name));
}
