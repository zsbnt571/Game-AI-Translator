using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// OpenCode 1.19.5 merges process.env with shell.env output. Override unwanted keys;
// deleting them from output would leave the inherited values in the command child.
// This applies to the native shell tool. Other native child processes (formatters,
// LSP/MCP) do not necessarily use this hook. It is not a filesystem/OS sandbox.
// Real API credentials therefore stay in AiOpenCodeModelProxy in the parent process.
internal sealed record AiOpenCodeShellEnvironment(string PluginUri, string MarkerPath, string Nonce, string ConfigRoot)
{
    internal static AiOpenCodeShellEnvironment Prepare(string run)
    {
        string root = Path.Combine(run, "native-config"), child = Path.Combine(run, "command-home");
        foreach (string directory in new[] { root, child, Path.Combine(child, "tmp"), Path.Combine(child, "config"), Path.Combine(child, "data") })
        { AppDataPaths.RejectReparsePoints(directory); Directory.CreateDirectory(directory); }
        string path = Path.Combine(root, "fusion-shell-environment.mjs"), marker = Path.Combine(root, "loaded.txt");
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(file, new UTF8Encoding(false))) writer.Write(Script(child, marker, nonce));
        return new(new Uri(path).AbsoluteUri, marker, nonce, root);
    }

    internal bool Loaded() => File.Exists(MarkerPath) && File.ReadAllText(MarkerPath) == Nonce;

    internal static string Script(string childHome, string marker, string nonce) => """
import { writeFileSync } from 'node:fs';
import path from 'node:path';
const childHome = __CHILD_HOME__;
const keep = new Set(['SYSTEMROOT', 'WINDIR', 'COMSPEC', 'PATH', 'PATHEXT', 'PROCESSOR_ARCHITECTURE', 'NUMBER_OF_PROCESSORS']);
export default async () => {
  const hooks = {
    // This runs after native project discovery. Keep project agents, skills, MCP
    // and instructions, while the active Fusion connection and approval choice
    // remain authoritative. These are configuration rules, not a plugin sandbox.
    config: async config => {
      const active = JSON.parse(process.env.OPENCODE_CONFIG_CONTENT);
      for (const name of ['model', 'small_model', 'default_agent', 'enabled_providers', 'permission'])
        config[name] = structuredClone(active[name]);
      if (active.provider) config.provider = structuredClone(active.provider);
      else delete config.provider;
      config.mcp = { ...config.mcp, fusion: structuredClone(active.mcp.fusion) };
      config.agent ??= {};
      for (const name of ['build', 'plan']) {
        config.agent[name] ??= {};
        config.agent[name].disable = false;
        config.agent[name].mode = 'primary';
      }
      for (const [name, agent] of Object.entries(config.agent)) {
        agent.model = active.model;
        // Native plan's edit/general-task restrictions must survive project
        // wildcard overrides. Its built-in permission rules remain unchanged.
        const permission = typeof agent.permission === 'object' ? agent.permission : {};
        agent.permission = {
          ...Object.fromEntries(Object.entries(permission).filter(([key]) =>
            !(key in active.permission) && !(name === 'plan' && ['*', 'edit', 'task'].includes(key)))),
          ...structuredClone(active.permission)
        };
      }
    },
    'shell.env': async (_input, output) => {
      const clean = {};
      // Include existing plugin output so no earlier hook can forward a credential.
      for (const key of new Set([...Object.keys(process.env), ...Object.keys(output.env)]))
        clean[key] = keep.has(key.toUpperCase()) ? (process.env[key] ?? '') : '';
      Object.assign(clean, {
        HOME: childHome, USERPROFILE: childHome,
        APPDATA: path.join(childHome, 'appdata'), LOCALAPPDATA: path.join(childHome, 'localappdata'),
        TEMP: path.join(childHome, 'tmp'), TMP: path.join(childHome, 'tmp'),
        XDG_CONFIG_HOME: path.join(childHome, 'config'), XDG_DATA_HOME: path.join(childHome, 'data'),
        XDG_CACHE_HOME: path.join(childHome, 'cache'), XDG_STATE_HOME: path.join(childHome, 'state'),
        CODEX_HOME: path.join(childHome, 'codex'), OPENCODE_TEST_HOME: childHome,
        FUSION_HARNESS_API_KEY: '', OPENCODE_CONFIG_CONTENT: '', OPENCODE_SERVER_PASSWORD: '', OPENCODE_SERVER_USERNAME: ''
      });
      output.env = clean;
    }
  };
  writeFileSync(__MARKER__, __NONCE__, { encoding: 'utf8', flag: 'wx' });
  return hooks;
};
""".Replace("__CHILD_HOME__", JsonSerializer.Serialize(childHome)).Replace("__MARKER__", JsonSerializer.Serialize(marker)).Replace("__NONCE__", JsonSerializer.Serialize(nonce));
}
