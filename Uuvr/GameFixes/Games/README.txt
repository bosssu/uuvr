L1 code game fixes go here.

1. Prefer L0: add GameProfiles/{id}.cfg + a line in GameProfiles/profiles.manifest
2. If you need Harmony / scene hooks, create YourGameFix.cs:

  namespace Uuvr.GameFixes.Games;
  [UuvrGameFix("yourid")]
  public sealed class YourGameFix : UuvrGameFixBase
  {
      public override string Id { get { return "yourid"; } }
      public override void OnBootstrap(GameFixContext ctx)
      {
          base.OnBootstrap(ctx);
          // PatchSelf();
      }
  }

Id must match the manifest id. Core Harmony does not scan this namespace.
