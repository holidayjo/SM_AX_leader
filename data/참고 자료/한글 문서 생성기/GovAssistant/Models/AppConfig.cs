namespace GovAssistant.Models;

public class AppConfig
{
    public string Provider { get; set; } = "openai";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "";
    public string LastInputText { get; set; } = "";
    public int WindowX { get; set; } = 100;
    public int WindowY { get; set; } = 100;
}
