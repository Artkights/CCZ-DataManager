namespace DataManager.App;

/// <summary>Both the executable and UI acceptance hosts use the generated production settings.</summary>
public static class ApplicationBootstrap
{
    public static void Initialize() => ApplicationConfiguration.Initialize();
}
