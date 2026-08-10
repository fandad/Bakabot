using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Bakabot.Helpers;

namespace Bakabot.Services;

/// <summary>
/// 基础包自动修补服务
/// </summary>
public class PatchService
{
    public void PatchInstance(string instanceName)
    {
        var srcDir = PathHelper.GetInstanceSrcDir(instanceName);
        var indexJs = Path.Combine(srcDir, "index.js");

        if (!File.Exists(indexJs))
            return;

        var content = File.ReadAllText(indexJs, Encoding.UTF8);
        var original = content;

        content = StripLegacyChatPlugin(content);
        content = PatchResourcePackAutoAccept(content);
        content = PatchEnvPatch(content);
        content = PatchChatTrigger(content);
        content = PatchMsgTellMode(content);
        content = PatchCreateBotOptions(content);

        if (content != original)
        {
            var backupPath = indexJs + ".bak";
            if (!File.Exists(backupPath))
                File.Copy(indexJs, backupPath, overwrite: true);

            File.WriteAllText(indexJs, content, Encoding.UTF8);
        }

        // 行动文件（src/actions、plugins）的公屏播报门禁（由 SUPPRESS_ACTION_CHAT 开关控制）
        PatchActionChatGate(srcDir);

        // utils/chat.js 的 /msg 私信模式支持（TELL_MODE=msg）
        PatchTellModeChatUtils(srcDir);
    }

    /// <summary>
    /// 强制刷新实例补丁：先剥离已注入的补丁块，再重新应用最新版本。
    /// 用于补丁代码更新后，无需重建实例即可让旧实例生效。
    /// </summary>
    public void RepatchInstance(string instanceName)
    {
        var srcDir = PathHelper.GetInstanceSrcDir(instanceName);
        var indexJs = Path.Combine(srcDir, "index.js");

        if (!File.Exists(indexJs))
            return;

        var content = File.ReadAllText(indexJs, Encoding.UTF8);

        // 剥离旧的补丁块（以标记注释为边界；兼容改名前的 ARCbot 旧标记与改名后的 Bakabot 新标记）
        content = Regex.Replace(content,
            @"\r?\n?// ===== (?:Bakabot|ARCbot) 核心补丁 __(?:bakabot|arcbot)_env_patch__ =====[\s\S]*?// ===== END (?:Bakabot|ARCbot) 核心补丁 =====\r?\n?", "");
        content = Regex.Replace(content,
            @"\r?\n?// ===== (?:Bakabot|ARCbot) 资源包自动接受补丁 __(?:bakabot|arcbot)_rp_auto_accept__ =====[\s\S]*?// ===== END (?:Bakabot|ARCbot) 资源包自动接受补丁 =====\r?\n?", "");
        content = Regex.Replace(content,
            @"\r?\n?// ===== (?:Bakabot|ARCbot) 聊天触发模式补丁 __(?:bakabot|arcbot)_chat_trigger__ =====[\s\S]*?// ===== END (?:Bakabot|ARCbot) 聊天触发模式补丁 =====\r?\n?", "");
        content = StripLegacyChatPlugin(content);

        // 重新注入最新补丁
        content = PatchResourcePackAutoAccept(content);
        content = PatchEnvPatch(content);
        content = PatchChatTrigger(content);
        content = PatchMsgTellMode(content);

        File.WriteAllText(indexJs, content, Encoding.UTF8);

        // 行动文件的公屏播报门禁同步刷新（内部自行先剥离旧注入再重新应用）
        PatchActionChatGate(srcDir);

        // /msg 私信模式补丁同步刷新（幂等，已有标记则跳过）
        PatchTellModeChatUtils(srcDir);
    }

    /// <summary>
    /// 将基础包 zip 中的新增/更新文件同步到已有实例（用于旧实例不重建也能获得新功能）：
    /// - src/ 以基础包为准全量覆盖（启动器补丁块随后由 RepatchInstance 重新注入）
    /// - package.json 覆盖为基础包版本
    /// - plugins/ 只覆盖/新增基础包里存在的文件，用户自行导入的插件不受影响
    /// - node_modules/ 只补齐缺失的顶层依赖，不覆盖已有包
    /// </summary>
    public void SyncBaseFiles(string instanceName, string zipPath)
    {
        var instanceDir = PathHelper.GetInstanceDir(instanceName);
        if (!Directory.Exists(instanceDir) || !File.Exists(zipPath))
            return;
    
        var tmp = Path.Combine(Path.GetTempPath(), "bakabot_sync_" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(zipPath, tmp);
    
            // src/ 全量同步
            var srcFrom = Path.Combine(tmp, "src");
            if (Directory.Exists(srcFrom))
                CopyDirOverwrite(srcFrom, Path.Combine(instanceDir, "src"));
    
            // package.json（声明新增依赖）
            var pkg = Path.Combine(tmp, "package.json");
            if (File.Exists(pkg))
                File.Copy(pkg, Path.Combine(instanceDir, "package.json"), overwrite: true);
    
            // plugins/ 只同步基础包里存在的文件（用户导入的插件不动）
            var pluginsFrom = Path.Combine(tmp, "plugins");
            var pluginsTo = Path.Combine(instanceDir, "plugins");
            if (Directory.Exists(pluginsFrom))
            {
                Directory.CreateDirectory(pluginsTo);
                foreach (var file in Directory.EnumerateFiles(pluginsFrom, "*", SearchOption.AllDirectories))
                {
                    var dest = Path.Combine(pluginsTo, Path.GetRelativePath(pluginsFrom, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, overwrite: true);
                }
            }
    
            // node_modules/ 只补缺失的顶层包（含 @scope）
            var nmFrom = Path.Combine(tmp, "node_modules");
            var nmTo = Path.Combine(instanceDir, "node_modules");
            if (Directory.Exists(nmFrom) && Directory.Exists(nmTo))
            {
                foreach (var dir in Directory.EnumerateDirectories(nmFrom))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith('.')) continue;
                    if (name.StartsWith('@'))
                    {
                        var destScope = Path.Combine(nmTo, name);
                        Directory.CreateDirectory(destScope);
                        foreach (var sub in Directory.EnumerateDirectories(dir))
                        {
                            var destSub = Path.Combine(destScope, Path.GetFileName(sub));
                            if (!Directory.Exists(destSub))
                                CopyDirOverwrite(sub, destSub);
                        }
                    }
                    else
                    {
                        var dest = Path.Combine(nmTo, name);
                        if (!Directory.Exists(dest))
                            CopyDirOverwrite(dir, dest);
                    }
                }
            }
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
            catch { /* 临时目录清理失败不影响功能 */ }
        }
    }
    
    private static void CopyDirOverwrite(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
    
    /// <summary>
    /// 剥离旧版“用户名伪装”聊天插件（__arcbotChatPlugin/__bakabotChatPlugin 整块 plugins 注入）。
    /// </summary>
    private static string StripLegacyChatPlugin(string content)
    {
        if (!content.Contains("__bakabotChatPlugin") && !content.Contains("__arcbotChatPlugin"))
            return content;
        return Regex.Replace(content,
            @"\r?\n    plugins: \{\r?\n        __(?:arcbot|bakabot)ChatPlugin:[\s\S]*?\r?\n    \},\r?\n", "\n");
    }

    private string PatchEnvPatch(string content)
    {
        if (content.Contains("__bakabot_env_patch__"))
            return content;

        var patchCode = @"
// ===== Bakabot 核心补丁 __bakabot_env_patch__ =====
(function(){
  var __v = process.env.MC_VERSION;
  var __S = {
    '1.8':1,'1.8.8':1,'1.8.9':1,'1.9':1,'1.9.4':1,'1.10':1,'1.10.2':1,'1.11':1,'1.11.2':1,
    '1.12':1,'1.12.1':1,'1.12.2':1,'1.13':1,'1.13.2':1,'1.14':1,'1.14.4':1,'1.15':1,'1.15.2':1,
    '1.16':1,'1.16.1':1,'1.16.2':1,'1.16.3':1,'1.16.4':1,'1.16.5':1,'1.17':1,'1.17.1':1,
    '1.18':1,'1.18.1':1,'1.18.2':1,'1.19':1,'1.19.1':1,'1.19.2':1,'1.19.3':1,'1.19.4':1,
    '1.20':1,'1.20.1':1,'1.20.2':1,'1.20.3':1,'1.20.4':1,'1.20.5':1,'1.20.6':1,
    '1.21':1,'1.21.1':1,'1.21.2':1,'1.21.3':1,'1.21.4':1,'1.21.5':1,'1.21.6':1,
    '1.21.7':1,'1.21.8':1,'1.21.9':1,'1.21.10':1,'1.21.11':1
  };
  if (__v && !__S[__v]) {
    process.env.MC_VERSION = '1.20.1';
  }
  if (process.env.YGGDRASIL_ACCESS_TOKEN) {
    process.env.MC_AUTH_TYPE = 'mojang';
  }
})();
// ===== END Bakabot 核心补丁 =====
";
        return patchCode + content;
    }

    /// <summary>
    /// 资源包自动接受补丁。
    /// mineflayer 4.35.0 没有 autoAcceptResourcePack 选项，
    /// 因此通过包装 createBot，监听 resourcePack 事件后自动调用 bot.acceptResourcePack()。
    /// 配合 ViaProxy 的 --fake-accept-resource-packs，可覆盖强制资源包服务器的所有场景。
    /// </summary>
    private string PatchResourcePackAutoAccept(string content)
    {
        if (content.Contains("__bakabot_rp_auto_accept__"))
            return content;

        var patchCode = @"
// ===== Bakabot 资源包自动接受补丁 __bakabot_rp_auto_accept__ =====
(function(){
  try {
    var __mf = require('mineflayer');
    if (__mf.__bakabotRpPatched) return;
    __mf.__bakabotRpPatched = true;
    var __origCreateBot = __mf.createBot;
    __mf.createBot = function() {
      var __bot = __origCreateBot.apply(this, arguments);
      try {
        __bot.on('resourcePack', function() {
          try {
            // 事件触发时 dotenv 已加载 .env，此时判断才能正确读到用户配置
            if (process.env.AUTO_ACCEPT_RESOURCE_PACK === 'false') return;
            if (typeof __bot.acceptResourcePack === 'function') {
              __bot.acceptResourcePack();
              console.log('[Bakabot] 已自动接受服务器资源包');
            }
          } catch(e) {}
        });
      } catch(e) {}
      return __bot;
    };
  } catch(e) {}
})();
// ===== END Bakabot 资源包自动接受补丁 =====
";
        return patchCode + content;
    }

    /// <summary>
    /// 聊天触发模式补丁（三模式）：
    /// - owner_only   只听原主人
    /// - keyword_only 只听关键词（任何人说关键词才响应）
    /// - hybrid       主人有求必应，他人需说关键词（默认）
    /// 实现原理：基础包的主人判断、回复对象（sendToOwner）均在调用时动态读取
    /// process.env.MC_OWNER_NAME，因此命中触发时把该变量切换为真实说话人，
    /// 回复与指令自然指向触发者；主人发言时恢复原主人身份。
    /// 同时包装 LLMService.generatePlan，向提示词注入当前对话对象，
    /// 解决“我/过来/跟随我”等称呼仍指向原主人的问题。
    /// </summary>
    private string PatchChatTrigger(string content)
    {
        if (content.Contains("__bakabot_chat_trigger__"))
            return content;

        var patchCode = @"
// ===== Bakabot 聊天触发模式补丁 __bakabot_chat_trigger__ =====
(function(){
  try {
    var __state = { speaker: null };
    var __origOwnerEnv = null;
    var __ownersCache = null;

    // 主人列表只在首次捕获一次（此时 env 尚未被本补丁切换，
    // 读到的是真实主人配置），之后不再从 env 重读，避免被自身切换污染
    function __owners() {
      if (__ownersCache === null) {
        __origOwnerEnv = process.env.MC_OWNER_NAME;
        __ownersCache = (__origOwnerEnv || '').split(',').map(function(s){return s.trim();}).filter(Boolean);
      }
      return __ownersCache;
    }

    // 多主人规范化：基础包只认单一主人名，把 env 默认设为第一位主人，
    // 补丁内部保留完整名单用于放行判定；具体对话对象随消息动态切换
    function __normalizeOwnerEnv() {
      var list = __owners();
      if (list.length > 0) process.env.MC_OWNER_NAME = list[0];
    }

    function __cfg() {
      var owners = __owners();
      var mode = String(process.env.TRIGGER_MODE || 'hybrid').toLowerCase();
      var trigger = String(process.env.CHAT_TRIGGER || '').trim().toLowerCase();
      if (mode !== 'owner_only' && mode !== 'keyword_only') mode = 'hybrid';
      return { owners: owners, mode: mode, trigger: trigger };
    }
    function __isOwnerName(name, owners) {
      if (!name) return false;
      var ln = String(name).toLowerCase();
      for (var i = 0; i < owners.length; i++) {
        if (owners[i].toLowerCase() === ln) return true;
      }
      return false;
    }
    function __hasTrigger(msg, trigger) {
      if (!trigger || msg === undefined || msg === null) return false;
      return String(msg).toLowerCase().indexOf(trigger) !== -1;
    }
    function __parseWhisper(str) {
      var s = String(str);
      var m = s.match(/^([a-zA-Z0-9_]+)\s*(?:whispers to you:|悄悄地对你说\s*[:：])\s*(.*)$/);
      // 插件 /msg 类私信的常见格式：[发送者 箭头 接收者]内容
      // 箭头按码区匹配（U+2190-21FF 箭头区 / U+2794-27BE 装饰箭头区，
      // 涵盖 ➥➦➡➜→ 等极易混淆的变体），另兼容 -> 与 ~
      if (!m) m = s.match(/^\[([a-zA-Z0-9_]+)\s*(?:[\u2190-\u21FF\u2794-\u27BE]|->|~)\s*[^\]]*\]\s*(.+)$/);
      if (!m) return null;
      // 忽略机器人自己发出的私信回显（[自己 箭头 别人]），避免自我触发
      var __self = __state.bot && __state.bot.username;
      if (__self && m[1].toLowerCase() === String(__self).toLowerCase()) return null;
      return { speaker: m[1], msg: m[2] };
    }
    // 判定消息处置方式：null(无关) / owner / speaker / block
    function __gate(eventName, args) {
      var speaker = null, msg = null;
      if (eventName === 'chat') { speaker = args[0]; msg = args[1]; }
      else if (eventName === 'messagestr') {
        var w = __parseWhisper(args[0]);
        if (!w) return null;
        speaker = w.speaker; msg = w.msg;
      } else return null;
      if (!speaker) return null;
      var c = __cfg();
      if (c.owners.length === 0) return null;
      var owner = __isOwnerName(speaker, c.owners);
      var trig = __hasTrigger(msg, c.trigger);
      var allow = c.mode === 'owner_only' ? owner
                : c.mode === 'keyword_only' ? trig
                : (owner || trig);
      if (!allow) return { verdict: 'block', speaker: speaker };
      __state.speaker = speaker;
      return { verdict: owner ? 'owner' : 'speaker', speaker: speaker };
    }

    // LLM 上下文修正：让指令中的“我/主人”指向当前说话人。
    // 注意：LLMService 在 require 时就实例化并固化 apiKey，而本补丁位于
    // dotenv.config() 之前，因此必须延迟到 createBot 调用时才 require，
    // 否则 .env 里的 LLM_API_KEY 尚未加载，apiKey 会永远为空
    function __wrapLlm() {
      try {
        var __llm = require('./services/LLMService');
        if (__llm && typeof __llm.generatePlan === 'function' && !__llm.__bakabotGenPatched) {
          __llm.__bakabotGenPatched = true;
          var __origGen = __llm.generatePlan;
          __llm.generatePlan = function(sysPrompt, history, userPrompt) {
            try {
              var c = __cfg();
              var sp = __state.speaker;
              // 说话人不是默认主人（他人触发或非首位主人）时，明确告知 LLM 当前对话对象
              if (sp && c.owners.length > 0 && String(sp).toLowerCase() !== c.owners[0].toLowerCase()) {
                sysPrompt = String(sysPrompt) + '\n\n# 当前对话对象（重要）\n当前正在与你对话的玩家是「' + sp + '」。对方话语中的“我”等称呼均指「' + sp + '」；所有需要指定玩家的动作（TeleportRequest 的 target、FollowPlayer 的 player_name 等）必须填写「' + sp + '」。';
              }
            } catch(e) {}
            return __origGen.call(this, sysPrompt, history, userPrompt);
          };
        }
      } catch(e) {}
    }

    // 多主人支持：基础包的 StrictAutoTeleportLogin 只认单一主人名，
    // 替换其 _onMessage 使其认得全部主人名单里的任何一位发起的传送
    function __wrapTpa() {
      try {
        var __TPA = require('./instincts/autoTeleportAndLogin');
        if (__TPA && __TPA.prototype && !__TPA.prototype.__bakabotTpaPatched) {
          __TPA.prototype.__bakabotTpaPatched = true;
          __TPA.prototype._onMessage = function(msgLike) {
            var raw = this._extractText ? this._extractText(msgLike) : String(msgLike || '');
            if (!raw) return;
            var parsed = this._parseTeleportRequest(raw);
            if (!parsed) return;
            var owners = __owners();
            var req = String(parsed.requester).toLowerCase();
            var matched = null;
            for (var i = 0; i < owners.length; i++) {
              if (String(owners[i]).toLowerCase() === req) { matched = owners[i]; break; }
            }
            if (!matched) {
              this._log('拒绝非主人请求: ' + parsed.requester + ' (' + parsed.type + ')');
              return;
            }
            this._log('识别到主人请求: ' + parsed.requester + ' (' + parsed.type + ')，准备自动接受');
            // 发起传送的主人成为当前服务对象（sendToOwner 等都会指向他）
            process.env.MC_OWNER_NAME = matched;
            this._tryAcceptTeleport();
          };
        }
      } catch(e) {}
    }

    // 为每个 bot 安装 emit 网关
    var __mf = require('mineflayer');
    if (!__mf.__bakabotGatePatched) {
      __mf.__bakabotGatePatched = true;
      var __origCreateBot = __mf.createBot;
      __mf.createBot = function() {
        var __bot = __origCreateBot.apply(this, arguments);
        __state.bot = __bot; // 供私信回显过滤使用
        // 此刻 index.js 已执行过 dotenv.config()，可以安全加载 LLMService
        __wrapLlm();
        // 多主人 TPA 识别
        __wrapTpa();
        // 多主人名单规范化（默认主人 = 第一位）
        try { __normalizeOwnerEnv(); } catch(e) {}
        try {
          var __origEmit = __bot.emit;
          __bot.emit = function(eventName) {
            var args = Array.prototype.slice.call(arguments, 1);
            var r = null;
            try { r = __gate(eventName, args); } catch(e) {}
            if (!r) return __origEmit.apply(__bot, arguments);
            if (r.verdict === 'block') {
              // 暂时毒化主人名让基础包的主人检查失败（不影响其他监听器）
              var __prev = process.env.MC_OWNER_NAME;
              process.env.MC_OWNER_NAME = '__ARCBOT_BLOCKED__';
              try { return __origEmit.apply(__bot, [eventName].concat(args)); }
              finally { process.env.MC_OWNER_NAME = __prev; }
            }
            // 主人或关键词触发：把当前服务对象切换为说话人本人，
            // 这样回复、指令、LLM 上下文都精确指向正在说话的这位（多主人可互相区分）
            process.env.MC_OWNER_NAME = r.speaker;
            console.log('[Bakabot] 当前服务对象切换为: ' + r.speaker);
            return __origEmit.apply(__bot, [eventName].concat(args));
          };
        } catch(e) {}
        return __bot;
      };
    }
  } catch(e) {}
})();
// ===== END Bakabot 聊天触发模式补丁 =====
";
        return patchCode + content;
    }

    /// <summary>
    /// /msg 私信模式补丁（index.js 内联修改，幂等）：
    /// 基础包的私信监听只在 TELL_MODE=whisper 时生效，且只认原版
    /// "whispers to you:" / "悄悄地对你说" 格式。本补丁：
    /// 1. 让 TELL_MODE=msg 也走私信监听分支；
    /// 2. 私信解析兼容插件 /msg 的箭头格式 "[发送者 ➦ 接收者]内容"（箭头按码区宽匹配）；
    /// 3. 过滤机器人自己发出的私信回显，避免自我触发。
    /// 回复用哪个指令由 utils/chat.js 的补丁（PatchTellModeChatUtils）决定。
    /// </summary>
    private static string PatchMsgTellMode(string content)
    {
        // 箭头正则统一写法：按码区匹配（含 ➥➦➡➜→ 等易混淆变体），用 \u 转义不依赖文件编码
        const string arrowRe2 = "    const __bakabotWhisperRe2 = /^\\[([a-zA-Z0-9_]+)\\s*(?:[\\u2190-\\u21FF\\u2794-\\u27BE]|->|~)\\s*[^\\]]*\\]\\s*(.+)$/; // __bakabot_msg_tell__ 插件 /msg 的 [发送者箭头接收者] 格式";

        if (!content.Contains("__bakabot_msg_tell__"))
        {
            // 1. msg 模式也处理私信（基础包只放行 whisper）
            content = content.Replace(
                "if (tellMode !== 'whisper') return; // 非私聊模式不处理 messagestr",
                "if (tellMode !== 'whisper' && tellMode !== 'msg') return; // 非私聊模式不处理 messagestr（__bakabot_msg_tell__: msg 模式同样处理私信）");

            // 2. 私信格式识别：原版悄悄话 + 箭头格式 [发送者 箭头 接收者]内容
            content = content.Replace(
                @"const whisperRegex = /^([a-zA-Z0-9_]+)\s*(?:whispers to you:|悄悄地对你说\s*[:：])\s*(.*)$/;",
                "const __bakabotWhisperRe1 = /^([a-zA-Z0-9_]+)\\s*(?:whispers to you:|悄悄地对你说\\s*[:：])\\s*(.*)$/; // __bakabot_msg_tell__\r\n" + arrowRe2);
            content = content.Replace(
                "const match = messageStr.match(whisperRegex);",
                "let match = messageStr.match(__bakabotWhisperRe1) || messageStr.match(__bakabotWhisperRe2); // __bakabot_msg_tell__");

            // 3. 忽略自己发出的私信回显（发送者 = 机器人自己）
            content = Regex.Replace(content,
                @"(// 如果不是悄悄话格式，直接忽略\r?\n\s*if \(!match\) return;)",
                "$1\r\n    // 忽略机器人自己发出的私信回显，避免自我触发 // __bakabot_msg_tell__\r\n" +
                "    if (bot.username && match[1].toLowerCase() === bot.username.toLowerCase()) return;");
        }

        // 归一化箭头正则（幂等升级）：早期版本箭头用枚举字符（➦）导致 ➥ 等变体不匹配，
        // 已打补丁的实例在此统一升级为码区宽匹配
        content = Regex.Replace(content,
            @"[ \t]*const __bakabotWhisperRe2 = /[^\n]*__bakabot_msg_tell__[^\n]*",
            arrowRe2);

        return content;
    }

    /// <summary>
    /// utils/chat.js 的 /msg 私信模式补丁（幂等）：
    /// sendToOwner 的私聊回复原本写死 /tell；打补丁后按 TELL_MODE 选择指令：
    /// whisper（默认）→ /tell，msg → /msg。适配只支持 /msg 的服务器插件。
    /// </summary>
    private static void PatchTellModeChatUtils(string srcDir)
    {
        var chatJs = Path.Combine(srcDir, "utils", "chat.js");
        if (!File.Exists(chatJs))
            return;

        var content = File.ReadAllText(chatJs, Encoding.UTF8);
        if (content.Contains("__bakabot_tell_mode_msg__"))
            return;

        const string original = "bot.chat(`/tell ${ownerName} ${message}`);";
        if (!content.Contains(original))
            return;

        content = content.Replace(original,
            "bot.chat(`${tellMode === 'msg' ? '/msg' : '/tell'} ${ownerName} ${message}`); // __bakabot_tell_mode_msg__: msg 模式用 /msg 回信");
        content = content.Replace(
            "// 私聊模式：使用 /tell",
            "// 私聊模式：whisper 用 /tell，msg 用 /msg（Bakabot 补丁）");

        File.WriteAllText(chatJs, content, Encoding.UTF8);
    }

    /// <summary>
    /// 行动公屏播报门禁补丁：
    /// 基础包在 actions 目录的每个行动里硬编码了大量 sendToOwner / bot.chat 播报
    /// （行动开始、完成、异常等），机器人每次行动都会不可避免地在公屏刷屏。
    /// 本补丁对这些文件注入环境变量门禁：SUPPRESS_ACTION_CHAT=true 时静默这些播报，
    /// 关闭时原样透传，行为完全不变。因此补丁始终应用，开关只改 .env 即可生效。
    /// 不受影响：大模型回复、登录/传送指令、StateMachine 的 retry/skip 交互提示。
    /// 例外（永不拦截）：/ 开头的游戏指令（如 /tpa，虽由 sendToOwner/bot.chat 发出
    /// 但实为功能指令而非播报）；LookAction.js 的输出是主人主动询问的应答而非刷屏。
    /// </summary>
    private static void PatchActionChatGate(string srcDir)
    {
        var dirs = new[]
        {
            Path.Combine(srcDir, "actions"),
            Path.GetFullPath(Path.Combine(srcDir, "..", "plugins"))
        };
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.js", SearchOption.AllDirectories))
            {
                try { PatchActionFileGate(file); }
                catch { /* 单个文件失败不影响整体 */ }
            }
        }
    }

    private static void PatchActionFileGate(string file)
    {
        var content = File.ReadAllText(file, Encoding.UTF8);
        var original = content;
        var fileName = Path.GetFileName(file);

        // 先剥离上次注入的门禁代码，保证刷新幂等。
        // 注意：sendToOwner 注入是"替换"式（原 require 行被换成两行带标记代码），
        // 因此剥离必须还原出原始 require 行，不能直接删行
        content = Regex.Replace(content,
            "const\\s*\\{\\s*sendToOwner:\\s*__(?:bakabot|arcbot)RealSTO\\s*\\}\\s*=\\s*require\\((['\"][^'\"\\r\\n]*utils/chat['\"])\\)\\s*;[^\\r\\n]*\\r?\\n?\\s*const\\s+sendToOwner\\s*=\\s*function[^\\r\\n]*__(?:bakabot|arcbot)_action_chat_gate__\\r?\\n?",
            "const { sendToOwner } = require($1);\n");
        // 其余带标记的注入行（如 __bakabotSay 辅助函数）直接移除（兼容改名前的 arcbot 旧标记）
        if (content.Contains("__bakabot_action_chat_gate__") || content.Contains("__arcbot_action_chat_gate__"))
            content = Regex.Replace(content, @"[^\r\n]*__(?:bakabot|arcbot)_action_chat_gate__[^\r\n]*\r?\n?", "");
        content = content.Replace("__bakabotSay(this.bot, ", "this.bot.chat(");
        content = content.Replace("__arcbotSay(this.bot, ", "this.bot.chat(");

        // 门禁 sendToOwner 播报：把原函数重命名保留，同名替换为带开关的包装。
        // / 开头的内容是游戏指令（如 TeleportRequest 的 /tpa），无论开关如何都必须发送；
        // 其余动作层播报（含失败提示）一律静默——任务级失败反馈由不受门禁的
        // StateMachine（core 目录）统一上报，不在动作文件里开口子
        if (fileName != "LookAction.js")
        {
            content = Regex.Replace(content,
                "const\\s*\\{\\s*sendToOwner\\s*\\}\\s*=\\s*require\\((['\"][^'\"\\r\\n]*utils/chat['\"])\\)\\s*;",
                "const { sendToOwner: __bakabotRealSTO } = require($1); // __bakabot_action_chat_gate__\n" +
                "const sendToOwner = function (b, msg) { if (process.env.SUPPRESS_ACTION_CHAT === 'true' && !(typeof msg === 'string' && msg.startsWith('/'))) return; return __bakabotRealSTO.apply(null, arguments); }; // __bakabot_action_chat_gate__");
        }

        // 门禁行动文件里直接调用 bot.chat 的播报。
        // Command.js 的 bot.chat 是真正发送游戏指令，绝不能拦，因此排除；
        // 其余文件里 / 开头的内容同样视为指令透传
        if (fileName != "Command.js" && content.Contains("this.bot.chat("))
        {
            content = "function __bakabotSay(b, m) { if (process.env.SUPPRESS_ACTION_CHAT !== 'true' || (typeof m === 'string' && m.startsWith('/'))) b.chat(m); } // __bakabot_action_chat_gate__\n" + content;
            content = content.Replace("this.bot.chat(", "__bakabotSay(this.bot, ");
        }

        if (content != original)
            File.WriteAllText(file, content, Encoding.UTF8);
    }

    private string PatchCreateBotOptions(string content)
    {
        if (content.Contains("__bakabot_options_injected__"))
            return content;

        var pattern = @"(createBot\s*\(\s*\{)";
        var injection = @"$1
    // __bakabot_options_injected__
    autoAcceptResourcePack: process.env.AUTO_ACCEPT_RESOURCE_PACK !== 'false',
    ...(function(){
        if (!process.env.YGGDRASIL_ACCESS_TOKEN) return {};
        var base = (process.env.AUTH_SERVER_URL || 'https://littleskin.cn/api/yggdrasil').replace(/\/+$/, '');
        return {
            session: {
                accessToken: process.env.YGGDRASIL_ACCESS_TOKEN,
                clientToken: 'bakabot-yggdrasil',
                selectedProfile: {
                    id: process.env.YGGDRASIL_UUID,
                    name: process.env.YGGDRASIL_PLAYER_NAME
                }
            },
            username: process.env.YGGDRASIL_PLAYER_NAME,
            authServer: base + '/authserver',
            sessionServer: base + '/sessionserver'
        };
    })(),";

        return Regex.Replace(content, pattern, injection, RegexOptions.Multiline);
    }
}
