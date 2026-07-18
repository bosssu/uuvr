namespace Uuvr.GameFixes;

/// <summary>
/// Optional L1 code fix for a single game. L0 config profiles need no implementation.
/// </summary>
public interface IUuvrGameFix
{
    string Id { get; }

    void OnBootstrap(GameFixContext ctx);

    void OnCoreReady(UuvrCore core);

    void OnSceneLoaded(string sceneName, int buildIndex);

    void OnUnload();
}
