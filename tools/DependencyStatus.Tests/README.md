# Offline dependency status checks

This test links the production dependency reader and its pinned ZIP catalogue. It checks missing dependencies, the distinct version/backend/architecture/hash states, explicit local roots, sidecars, read-only verification and existing installer package selection. Synthetic bytes are used by default; no game or native library is executed and no network request is made.

```powershell
dotnet run --project tools/DependencyStatus.Tests/DependencyStatus.Tests.csproj -c Release
# Optional, after legally supplying and importing the exact eight pinned dependencies:
dotnet run --project tools/DependencyStatus.Tests/DependencyStatus.Tests.csproj -c Release -- --payload-root '<absolute-local-dependency-root>'
```

This is dependency validation, not evidence of all real-game compatibility or redistribution permission.
