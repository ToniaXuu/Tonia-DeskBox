namespace ToniaDeskBox.Models;

public class AppConfig
{
    public AppSettings Settings { get; set; } = new();
    public List<BoxConfig> Boxes { get; set; } = new();
}

public class AppSettings
{
    public bool Autorun { get; set; }
    public string Hotkey { get; set; } = "";
}

public class BoxConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "新建盒子";
    public double X { get; set; } = 120;
    public double Y { get; set; } = 120;
    public double Opacity { get; set; } = 0.85;
    public string BackColor { get; set; } = "#1F2A30";
    public bool Acrylic { get; set; }
    public bool Rounded { get; set; } = true;
    public double CornerSize { get; set; } = 8;
    public bool Topmost { get; set; }

    public List<string> Order { get; set; } = new();
}
