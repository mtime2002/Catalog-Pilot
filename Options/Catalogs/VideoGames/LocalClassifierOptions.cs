namespace CatalogPilot.Options;

public sealed class LocalClassifierOptions
{
    public const string SectionName = "LocalClassifier";

    public bool Enabled { get; set; } = true;

    public string PythonExecutablePath { get; set; } = "python";

    public string PaddleScriptPath { get; set; } = "scripts/paddle_ocr.py";

    public string BarcodeScriptPath { get; set; } = "scripts/barcode_scan.py";

    public int MaxImages { get; set; } = 4;

    public int ProcessTimeoutSeconds { get; set; } = 20;
}
