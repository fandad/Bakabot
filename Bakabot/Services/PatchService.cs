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
    /// <summary>拆分版自动传送/登录/死亡回城本能源码（按 .env 开关独立控制）</summary>
    private const string TpAcceptInstinctSource = @"
/**
 * 自动传送 / 自动登录 / 死亡回城（拆分版）
 *
 * 三个功能由启动器“行动设置”独立控制（.env 写入）：
 * - 接受 TPA（INSTINCT_TP_ACCEPT）：收到传送请求自动 /tpaccept
 *     · TPA_OWNER_ONLY=true（默认）：仅接受主人请求
 *     · TPA_OWNER_ONLY=false：所有人都自动接受（慎用）
 *     · TPA_ACCEPT_TRIGGER：服务器消息关键词（尽量写全），逗号分隔，包含即触发；留空只识别默认格式
 * - 自动登录（INSTINCT_AUTO_LOGIN）：进服后 /l 密码
 * - 死亡 back（INSTINCT_DEATH_BACK）：死亡后 /back
 */
const { sendToOwner } = require('../utils/chat');

class StrictAutoTeleportLogin {
  constructor(bot, config = {}) {
    this.bot = bot;

    // 支持逗号分隔的多主人名单
    this.ownerNames = String(config.ownerName || process.env.MC_OWNER_NAME || '')
      .split(/[,，]/)
      .map(function (s) { return s.trim(); })
      .filter(Boolean);
    this.ownerNameLower = String(this.ownerNames[0] || '').toLowerCase();
    this.loginPassword = config.loginPassword || process.env.MC_LOGIN_PASSWORD || '';

    // 功能开关（优先 config，兜底读 .env）
    this.acceptEnabled = config.acceptEnabled !== undefined ? config.acceptEnabled : (process.env.INSTINCT_TP_ACCEPT === 'true');
    this.ownerOnly = config.ownerOnly !== undefined ? config.ownerOnly : (process.env.TPA_OWNER_ONLY !== 'false');
    this.loginEnabled = config.loginEnabled !== undefined ? config.loginEnabled : (process.env.INSTINCT_AUTO_LOGIN === 'true');
    this.backEnabled = config.backEnabled !== undefined ? config.backEnabled : (process.env.INSTINCT_DEATH_BACK === 'true');

    // 关键词触发（逗号分隔，包含即触发）
    this.triggerKeywords = String(config.triggerKeywords || process.env.TPA_ACCEPT_TRIGGER || '')
      .split(/[,，]/)
      .map(function (s) { return s.trim(); })
      .filter(Boolean);

    this.acceptCommand = config.acceptCommand || '/tpaccept';
    this.backCommand = config.backCommand || '/back';

    this.loginDelay = config.loginDelay !== undefined ? config.loginDelay : 5000;
    this.freezeBeforeAccept = config.freezeBeforeAccept !== undefined ? config.freezeBeforeAccept : 300;
    this.freezeAfterAccept = config.freezeAfterAccept !== undefined ? config.freezeAfterAccept : 1200;
    this.acceptCooldown = config.acceptCooldown !== undefined ? config.acceptCooldown : 2000;
    this.backDelay = config.backDelay !== undefined ? config.backDelay : 2000;

    this.debug = config.debug !== undefined ? config.debug : true;

    this._accepting = false;
    this._lastAcceptAt = 0;
    this._dead = false;
    this._didLoginSequence = false;
    this._timers = new Set();

    this._onMessage = this._onMessage.bind(this);
    this._onSpawn = this._onSpawn.bind(this);
    this._onLogin = this._onLogin.bind(this);
    this._onDeath = this._onDeath.bind(this);
  }

  mount() {
    this.bot.on('message', this._onMessage);
    this.bot.on('messagestr', this._onMessage);
    this.bot.on('spawn', this._onSpawn);
    this.bot.on('login', this._onLogin);
    this.bot.on('death', this._onDeath);

    this._log('模块已挂载');
    this._log('接受TPA=' + this.acceptEnabled + ' 仅主人=' + this.ownerOnly + ' 自动登录=' + this.loginEnabled + ' 死亡back=' + this.backEnabled);
    if (this.acceptEnabled && this.triggerKeywords.length > 0) {
      this._log('TPA触发关键词: ' + this.triggerKeywords.join(' | '));
    }
  }

  unmount() {
    this.bot.off('message', this._onMessage);
    this.bot.off('messagestr', this._onMessage);
    this.bot.off('spawn', this._onSpawn);
    this.bot.off('login', this._onLogin);
    this.bot.off('death', this._onDeath);

    for (const t of this._timers) clearTimeout(t);
    this._timers.clear();

    this._log('模块已卸载');
  }

  _log() {
    if (!this.debug) return;
    var args = Array.prototype.slice.call(arguments);
    args.unshift('[StrictTP+Login]');
    console.log.apply(console, args);
  }

  _setTimer(fn, ms) {
    const t = setTimeout(() => {
      this._timers.delete(t);
      fn();
    }, ms);
    this._timers.add(t);
    return t;
  }

  _sleep(ms) {
    return new Promise((resolve) => this._setTimer(resolve, ms));
  }

  _extractText(msgLike) {
    if (typeof msgLike === 'string') return msgLike;
    if (!msgLike) return '';
    if (typeof msgLike.toString === 'function') return msgLike.toString();
    return String(msgLike);
  }

  _stripColorCodes(s) {
    return (s || '').replace(/§[0-9a-fk-or]/gi, '');
  }

  _normalizeSpaces(s) {
    return (s || '').replace(/\s+/g, ' ').trim();
  }

  /**
   * 解析传送请求发起人（标准格式，兼容主流 EssentialsX）：
   * - 玩家名 请求传送到你这里
   * - 玩家名 请求传送到你的位置
   * - 玩家名 请求传送到他们的位置
   * - 玩家名 has requested to teleport to you
   * - 玩家名 has requested that you teleport to them
   */
  _parseTeleportRequest(rawText) {
    const clean = this._normalizeSpaces(this._stripColorCodes(rawText));
    if (!clean) return null;

    let m = clean.match(/^([\u4e00-\u9fa5A-Za-z0-9_]{2,16})\s*请求传送到你这里?/);
    if (m) return { requester: m[1], type: 'tpa' };

    m = clean.match(/^([\u4e00-\u9fa5A-Za-z0-9_]{2,16})\s*请求传送到你的位置?/);
    if (m) return { requester: m[1], type: 'tpa' };

    m = clean.match(/^([\u4e00-\u9fa5A-Za-z0-9_]{2,16})\s*请求传送到他们的位置?/);
    if (m) return { requester: m[1], type: 'tpahere' };

    m = clean.match(/^([\u4e00-\u9fa5A-Za-z0-9_]{2,16})\s*has requested to teleport to you/i);
    if (m) return { requester: m[1], type: 'tpa' };

    m = clean.match(/^([\u4e00-\u9fa5A-Za-z0-9_]{2,16})\s*has requested that you teleport to them/i);
    if (m) return { requester: m[1], type: 'tpahere' };

    return null;
  }

  _containsTrigger(raw) {
    if (this.triggerKeywords.length === 0) return false;
    const lower = String(raw).toLowerCase();
    return this.triggerKeywords.some(function (k) {
      return k && lower.indexOf(k.toLowerCase()) !== -1;
    });
  }

  _onMessage(msgLike) {
    if (!this.acceptEnabled) return;
    const raw = this._extractText(msgLike);
    if (!raw) return;

    const parsed = this._parseTeleportRequest(raw);
    const triggered = !!parsed || this._containsTrigger(raw);
    if (!triggered) return;

    // 仅主人模式
    if (this.ownerOnly) {
      if (parsed) {
        var matchedOwner = this.ownerNames.some(function (n) {
          return n && n.toLowerCase() === parsed.requester.toLowerCase();
        });
        if (!matchedOwner) {
          this._log('拒绝非主人请求: ' + parsed.requester + ' (' + parsed.type + ')');
          return;
        }
      } else {
        // 关键词命中但解析不到玩家名：只有消息里包含某个主人名才接受
        var lowerRaw = String(raw).toLowerCase();
        var hitOwner = this.ownerNames.some(function (n) {
          return n && lowerRaw.indexOf(n.toLowerCase()) !== -1;
        });
        if (!hitOwner) {
          this._log('关键词命中但消息里没有主人名，忽略');
          return;
        }
      }
      this._log('识别到主人请求 (' + (parsed ? parsed.type : '关键词') + ')，准备自动接受');
    } else {
      this._log('自动接受 TPA（所有人模式）: ' + (parsed ? parsed.requester : '关键词命中'));
    }

    this._tryAcceptTeleport();
  }

  async _tryAcceptTeleport() {
    const now = Date.now();
    if (this._accepting) return;
    if (this._dead) return;
    if (now - this._lastAcceptAt < this.acceptCooldown) {
      this._log('命中接受冷却，跳过本次');
      return;
    }

    this._accepting = true;
    this._lastAcceptAt = now;

    try {
      this._freezeMovement();
      this._log('已冻结移动，' + this.freezeBeforeAccept + 'ms 后发送 ' + this.acceptCommand);

      await this._sleep(this.freezeBeforeAccept);
      sendToOwner(this.bot, this.acceptCommand);
      this._log('已发送：' + this.acceptCommand);

      this._log('继续冻结 ' + this.freezeAfterAccept + 'ms，等待传送稳定');
      await this._sleep(this.freezeAfterAccept);
    } catch (err) {
      this._log('自动接受失败:', err && err.message);
    } finally {
      this._unfreezeMovement();
      this._accepting = false;
      this._log('传送接受流程结束');
    }
  }

  _onLogin() {
    this._log('收到 login 事件 (已连接到服务器)');
    this._startLoginSequence('login');
  }

  _onSpawn() {
    this._dead = false;
    this._log('收到 spawn 事件 (已进入世界)');
    this._startLoginSequence('spawn');
  }

  _startLoginSequence(source) {
    if (!this.loginEnabled) {
      this._log('自动登录未开启，跳过 (' + source + ')');
      this._didLoginSequence = true;
      return;
    }
    if (this._didLoginSequence) {
      this._log('登录流程已执行过，本次 ' + source + ' 跳过');
      return;
    }
    if (!this.loginPassword) {
      this._log('警告：未配置 loginPassword，跳过 /l (来自 ' + source + ')');
      this._didLoginSequence = true;
      return;
    }

    this._didLoginSequence = true;
    this._log('登录流程启动 (触发源 ' + source + ')，' + this.loginDelay + 'ms 后发送 /l ******');

    this._setTimer(() => {
      try {
        sendToOwner(this.bot, '/l ' + this.loginPassword);
        this._log('步骤1完成：已发送 /l ******');
      } catch (err) {
        this._log('步骤1失败：发送 /l 失败 ->', err && err.message);
      }
    }, this.loginDelay);
  }

  _onDeath() {
    if (!this.backEnabled) {
      this._log('死亡 back 未开启，跳过');
      return;
    }
    this._dead = true;
    this._log('检测到死亡，' + this.backDelay + 'ms 后执行 ' + this.backCommand);

    this._setTimer(() => {
      try {
        sendToOwner(this.bot, this.backCommand);
        this._log('已发送：' + this.backCommand);
      } catch (err) {
        this._log('发送 ' + this.backCommand + ' 失败:', err && err.message);
      }
    }, this.backDelay);
  }

  _freezeMovement() {
    const states = ['forward', 'back', 'left', 'right', 'jump', 'sprint', 'sneak'];
    for (const s of states) this.bot.setControlState(s, false);

    if (this.bot.pathfinder && typeof this.bot.pathfinder.isMoving === 'function' && this.bot.pathfinder.isMoving()) {
      try {
        this.bot.pathfinder.stop();
        this._log('已停止 pathfinder 移动');
      } catch (_) {}
    }
  }

  _unfreezeMovement() {
    // 不恢复按键状态，交给上层任务系统重新下发
  }
}

module.exports = StrictAutoTeleportLogin;
";

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
        content = PatchQQBridgeV2(content);
        content = PatchTpSplitMount(content);
        content = PatchQuickCommands(content);
        content = PatchStartupGreeting(content);
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
        PatchTpAcceptInstinct(srcDir);
        PatchLlmService(srcDir);
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
        content = PatchQQBridgeV2(content);
        content = PatchTpSplitMount(content);
        content = PatchQuickCommands(content);
        content = PatchStartupGreeting(content);
        content = PatchCreateBotOptions(content);

        File.WriteAllText(indexJs, content, Encoding.UTF8);

        // 行动文件的公屏播报门禁同步刷新（内部自行先剥离旧注入再重新应用）
        PatchActionChatGate(srcDir);

        // /msg 私信模式补丁同步刷新（幂等，已有标记则跳过）
        PatchTellModeChatUtils(srcDir);
        PatchTpAcceptInstinct(srcDir);
        PatchLlmService(srcDir);
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
    /// 进服问候语开关补丁（幂等）：INSTINCT_STARTUP_GREETING=false 时不再发送
    /// “主人好，底层框架已启动，等待指令！”，默认开启。
    /// </summary>
    private static string PatchStartupGreeting(string content)
    {
        if (content.Contains("__bakabot_greeting_toggle__"))
            return content;

        const string original = "sendToOwner(bot, '主人好，底层框架已启动，等待指令！');";
        if (!content.Contains(original))
            return content;

        return content.Replace(original,
            "if (process.env.INSTINCT_STARTUP_GREETING !== 'false') sendToOwner(bot, '主人好，底层框架已启动，等待指令！'); // __bakabot_greeting_toggle__");
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
                "const sendToOwner = function (b, msg) { if (process.env.SUPPRESS_ACTION_CHAT === 'true' && !(typeof msg === 'string' && msg.startsWith('/')) && !(global.__bakabotQQ && global.__bakabotQQ.active)) return; return __bakabotRealSTO.apply(null, arguments); }; // __bakabot_action_chat_gate__");
        }

        // 门禁行动文件里直接调用 bot.chat 的播报。
        // Command.js 的 bot.chat 是真正发送游戏指令，绝不能拦，因此排除；
        // 其余文件里 / 开头的内容同样视为指令透传
        if (fileName != "Command.js" && content.Contains("this.bot.chat("))
        {
            content = "function __bakabotSay(b, m) { if (process.env.SUPPRESS_ACTION_CHAT !== 'true' || (typeof m === 'string' && m.startsWith('/')) || (global.__bakabotQQ && global.__bakabotQQ.active)) b.chat(m); } // __bakabot_action_chat_gate__\n" + content;
            content = content.Replace("this.bot.chat(", "__bakabotSay(this.bot, ");
        }

        if (content != original)
            File.WriteAllText(file, content, Encoding.UTF8);
    }

    /// <summary>
    /// QQ 桥接补丁（index.js 注入）：
    /// - 拦截控制台输入中 "QQCMD {json}" 行，直接送入 handleMessage 指令管道（不发送到游戏公屏）
    /// - 会话通道：QQ 指令到达时激活 QQ 通道（记录 QQ 号与绑定玩家名）；游戏内聊天事件到达时切回游戏通道
    /// - 输出分流：QQ 通道中，发给玩家的回复（含 /tell /msg）与普通聊天/播报 → stdout "[QQ-OUT] {json}"；/ 开头命令仍进游戏
    /// - LLM 上下文：注入当前 QQ 对话对象；未绑定玩家时提示无法执行需要指定玩家的指令
    /// </summary>
    private string PatchQQBridge(string content)
    {
        // 先剥离旧版 QQ 桥接补丁（Repatch 时保证能重新注入最新版）
        content = Regex.Replace(content,
            @"\r?\n?// ===== (?:Bakabot|ARCbot) QQ 桥接补丁 __(?:bakabot|arcbot)_qq_bridge__ =====[\s\S]*?// ===== END (?:Bakabot|ARCbot) QQ 桥接补丁 =====\r?\n?", "");

        // 剥离失败（无 END 标记等异常情况）时跳过，避免重复注入
        if (content.Contains("__bakabot_qq_bridge__"))
            return content;

        var patchCode = @"
// ===== Bakabot QQ 桥接补丁 __bakabot_qq_bridge__ =====
(function(){
  try {
    if (global.__bakabotQQBridgePatched) return;
    global.__bakabotQQBridgePatched = true;

    // 当前 QQ 会话通道：{ active, qq, player }；游戏内消息到来时清空切回游戏
    global.__bakabotQQ = null;
    global.__bakabotQQBot = null;
    global.__bakabotQQReady = false;

    function __qqOut(type, text) {
      try { console.log('[QQ-OUT] ' + JSON.stringify({ type: type, text: String(text || '') })); } catch(e) {}
    }

    // ── 拦截控制台输入：QQCMD JSON 行 → QQ 指令管道（绝不 bot.chat 发公屏）──
    var __origCreateInterface = require('readline').createInterface;
    require('readline').createInterface = function(opts) {
      var __rl = __origCreateInterface.apply(this, arguments);
      try {
        var __origOn = __rl.on.bind(__rl);
        __rl.on = function(event, listener) {
          if (event === 'line') {
            var __origListener = listener;
            listener = function(line) {
              var t = String(line || '').trim();
              if (t.indexOf('QQCMD ') === 0) { __handleQQCmd(t); return; }
              return __origListener.apply(this, arguments);
            };
          }
          return __origOn(event, listener);
        };
      } catch(e) {}
      return __rl;
    };

    function __handleQQCmd(line) {
      var msg = null;
      try { msg = JSON.parse(line.slice('QQCMD '.length)); } catch(e) {}
      if (!msg || !msg.text) return;
      var qq = String(msg.qq || '');
      var player = String(msg.player || '').trim();
      if (qq) global.__bakabotQQ = { active: true, qq: qq, player: player || null };
      __deliverQQ(msg.text, player || qq);
    }

    // 直接把 QQ 指令送入基础包统一处理链（handleMessage 为主人判定 + LLM 规划）
    function __deliverQQ(text, speaker) {
      try {
        var bot = global.__bakabotQQBot;
        if (!bot || !bot.entity) { __qqOut('msg', '机器人尚未登录游戏，暂时无法执行指令，请稍后再试。'); return; }
        if (!global.__bakabotQQReady) { __qqOut('msg', '机器人正在准备中，请稍后再试。'); return; }
        __wrapQqLlm();
        // 说话人切换为 QQ 绑定玩家（或 QQ 号），handleMessage 的主人检查据此放行；
        // 保持到下一条指令（QQ 或游戏内），与聊天触发补丁的“服务对象切换”一致
        process.env.MC_OWNER_NAME = speaker;
        console.log('[QQ] 收到指令来自 ' + speaker + ': ' + text);
        var __p = handleMessage(speaker, text);
        if (__p && typeof __p.catch === 'function') __p.catch(function(e){ console.log('[QQ] 指令处理异常: ' + (e && e.message)); });
      } catch(e) {
        console.log('[QQ] 指令处理失败: ' + (e && e.message));
      }
    }

    // LLM 上下文：当前对话对象（QQ 用户）+ 未绑定提示
    function __wrapQqLlm() {
      try {
        var __llm = require('./services/LLMService');
        if (!__llm || typeof __llm.generatePlan !== 'function' || __llm.__bakabotQQGenPatched) return;
        __llm.__bakabotQQGenPatched = true;
        var __origGen = __llm.generatePlan;
        __llm.generatePlan = function(sysPrompt, history, userPrompt) {
          try {
            var qq = global.__bakabotQQ;
            if (qq && qq.active) {
              if (qq.player) {
                sysPrompt = String(sysPrompt) + '\n\n# 当前对话对象（QQ 用户）\n当前指令来自 QQ 用户「' + qq.player + '」（已绑定游戏 ID）。对方话语中的“我”等称呼均指「' + qq.player + '」；所有需要指定玩家的动作（TeleportRequest 的 target、FollowPlayer 的 player_name、DropItemAction 的 target_player 等）必须填写「' + qq.player + '」。';
              } else {
                sysPrompt = String(sysPrompt) + '\n\n# 当前对话对象（QQ 用户，未绑定）\n当前指令来自未绑定游戏 ID 的 QQ 用户。若指令需要指定玩家才能执行（如传送、跟随、丢物品给玩家），请直接回复：你还没有绑定游戏玩家，无法执行该指令。不要编造玩家名。';
              }
            }
          } catch(e) {}
          return __origGen.call(this, sysPrompt, history, userPrompt);
        };
      } catch(e) {}
    }

    // 包装 createBot：记录 bot、监听 spawn 就绪、输出分流、游戏消息切回游戏通道
    var __mf = require('mineflayer');
    if (!__mf.__bakabotQQCreatePatched) {
      __mf.__bakabotQQCreatePatched = true;
      var __origCreateBot = __mf.createBot;
      __mf.createBot = function() {
        var __bot = __origCreateBot.apply(this, arguments);
        global.__bakabotQQBot = __bot;
        try { __bot.once('spawn', function(){ global.__bakabotQQReady = true; }); } catch(e) {}

        // 输出分流：QQ 通道中，发给玩家的回复（/tell /msg）与普通聊天/播报 → [QQ-OUT]；/ 开头命令仍进游戏
        try {
          var __origChat = __bot.chat.bind(__bot);
          __bot.chat = function(message) {
            var s = String(message == null ? '' : message);
            var qq = global.__bakabotQQ;
            if (qq && qq.active) {
              if (s.indexOf('/tell ') === 0 || s.indexOf('/msg ') === 0) {
                var parts = s.split(' ');
                if (parts.length > 2) { parts.splice(0, 2); var txt = parts.join(' ').trim(); if (txt) __qqOut('msg', txt); }
                return;
              }
              if (s.charAt(0) !== '/') { __qqOut('msg', s); return; }
            }
            return __origChat(s);
          };
        } catch(e) {}

        // 游戏内聊天/私信事件到达 → 会话通道切回游戏
        try {
          var __origEmit = __bot.emit;
          __bot.emit = function(eventName) {
            if ((eventName === 'chat' || eventName === 'messagestr') && global.__bakabotQQ) {
              global.__bakabotQQ = null;
            }
            return __origEmit.apply(__bot, arguments);
          };
        } catch(e) {}
        return __bot;
      };
    }
  } catch(e) {}
})();
// ===== END Bakabot QQ 桥接补丁 =====
";
        return patchCode + content;
    }

    /// <summary>
    /// 拆分“登录自动传送”为三个独立开关（接受TPA/自动登录/死亡back）：
    /// 修正 index.js 的挂载条件——任一开关开启即挂载本能（旧开关 INSTINCT_AUTO_TP_LOGIN 仍兼容）。
    /// </summary>
    private static string PatchTpSplitMount(string content)
    {
        var pattern = @"if\s*\(process\.env\.INSTINCT_AUTO_TP_LOGIN\s*===\s*'true'\)\s*\{\s*const autoTPLogin = new AutoTeleportAndLogin\(bot, \{[\s\S]*?autoTPLogin\.mount\(\);\s*\}";
        var replacement = "if (process.env.INSTINCT_TP_ACCEPT === 'true' || process.env.INSTINCT_AUTO_LOGIN === 'true' || process.env.INSTINCT_DEATH_BACK === 'true' || process.env.INSTINCT_AUTO_TP_LOGIN === 'true') {\n" +
            "    const autoTPLogin = new AutoTeleportAndLogin(bot, {\n" +
            "        ownerName: process.env.MC_OWNER_NAME,\n" +
            "        loginPassword: process.env.MC_LOGIN_PASSWORD,\n" +
            "        loginDelay: 5000,\n" +
            "        freezeBeforeAccept: 300,\n" +
            "        freezeAfterAccept: 1200,\n" +
            "        backDelay: 2000,\n" +
            "        acceptCommand: '/tpaccept',\n" +
            "        backCommand: '/back',\n" +
            "        debug: true,\n" +
            "    });\n" +
            "    autoTPLogin.mount();\n" +
            "}";
        return Regex.Replace(content, pattern, replacement);
    }

    /// <summary>重写 instincts/autoTeleportAndLogin.js：三个功能独立开关 + TPA 关键词触发</summary>
    private static void PatchTpAcceptInstinct(string srcDir)
    {
        var file = Path.Combine(srcDir, "instincts", "autoTeleportAndLogin.js");
        if (!File.Exists(file)) return;
        File.WriteAllText(file, TpAcceptInstinctSource, Encoding.UTF8);
    }

    /// <summary>
    /// LLMService 补丁：
    /// 1) 解析 JSON 前先剥掉思考模型输出的 &lt;think&gt;...&lt;/think&gt; 整块（用新变量承接，不重赋值 const）；
    /// 2) system prompt 末尾追加“只输出 JSON，不要输出任何思考过程或解释”。
    /// 兼容修复：会先清掉旧版本误注入的“content = ...replace(...)”行。
    /// </summary>
    private static void PatchLlmService(string srcDir)
    {
        var file = Path.Combine(srcDir, "services", "LLMService.js");
        if (!File.Exists(file)) return;

        var content = File.ReadAllText(file, Encoding.UTF8);

        // 清理旧补丁行（含错误版本的重赋值行），保证可重复应用
        content = Regex.Replace(content, @"[^\r\n]*__bakabot_llm_patch__[^\r\n]*\r?\n?", "");
        content = Regex.Replace(content, @"[^\r\n]*content\s*=\s*String\(content \|\| ''\)\.replace[^\r\n]*\r?\n?", "");

        // 1. 解析前剥掉 <think>...</think>（新变量承接，不碰 const content）
        content = content.Replace(
            "let jsonString = content.trim();",
            "// __bakabot_llm_patch__: 剥掉思考模型输出的 <think>...</think> 整块，避免污染 JSON 解析\n" +
            "        let jsonString = String(content || '').replace(/<think>[\\s\\S]*?<\\/think>/gi, '').trim();");

        // 2. system prompt 强制只输出 JSON
        content = content.Replace(
            "{ role: 'system', content: systemPrompt }",
            "{ role: 'system', content: systemPrompt + '\\n\\n只输出 JSON，不要输出任何思考过程或解释。' }");

        File.WriteAllText(file, content, Encoding.UTF8);
    }

    /// <summary>
    /// 命令提示词补丁（index.js 注入）：
    /// 整句包含关键词时直接执行对应命令（不走 LLM），
    /// 配置来自实例根目录 quick_commands.json（启动器页面写入，每条消息实时重读）。
    /// </summary>
    private static string PatchQuickCommands(string content)
    {
        content = Regex.Replace(content,
            @"\r?\n?// ===== (?:Bakabot|ARCbot) 命令提示词补丁 __(?:bakabot|arcbot)_quick_cmds__ =====[\s\S]*?// ===== END (?:Bakabot|ARCbot) 命令提示词补丁 =====\r?\n?", "");

        if (content.Contains("__bakabot_quick_cmds__"))
            return content;

        var patchCode = @"
// ===== Bakabot 命令提示词补丁 __bakabot_quick_cmds__ =====
(function(){
  try {
    if (global.__bakabotQuickCmdsPatched) return;
    global.__bakabotQuickCmdsPatched = true;

    var __fs = require('fs');
    var __path = require('path');
    var __cmdFile = __path.join(__dirname, '..', 'quick_commands.json');

    function __loadQuickConfig() {
      try {
        if (!__fs.existsSync(__cmdFile)) return null;
        var data = JSON.parse(__fs.readFileSync(__cmdFile, 'utf8'));
        // 兼容旧格式：裸数组 [ {keyword,command}, ... ]
        if (Array.isArray(data)) {
          return { enabled: true, blockGame: false, blockQq: false, suppressGameReply: false, commands: data };
        }
        if (!data || typeof data !== 'object') return null;
        return {
          enabled: data.enabled !== false,
          blockGame: data.blockGame === true,
          blockQq: data.blockQq === true,
          suppressGameReply: data.suppressGameReply === true,
          commands: Array.isArray(data.commands) ? data.commands : []
        };
      } catch (e) { return null; }
    }

    function __runQuickCommand(username, message) {
      try {
        if (!message || !username) return false;
        var bot = global.__bakabotQQBot;
        // 机器人自己说的话不触发
        if (bot && bot.username && String(username).toLowerCase() === String(bot.username).toLowerCase()) return false;

        var cfg = __loadQuickConfig();
        if (!cfg || !cfg.enabled || !cfg.commands || cfg.commands.length === 0) return false;

        var lower = String(message).toLowerCase();
        var hit = null;
        for (var i = 0; i < cfg.commands.length; i++) {
          var kw = String(cfg.commands[i].keyword || '').trim();
          if (kw && lower.indexOf(kw.toLowerCase()) !== -1) { hit = cfg.commands[i]; break; }
        }
        if (!hit) return false;

        // 通道判断：QQ 会话通道激活 = QQ 触发，否则视为游戏内触发
        var ch = global.__bakabotQQ && global.__bakabotQQ.active ? global.__bakabotQQ : null;
        if (ch) {
          // QQ 屏蔽：命中即吞掉消息（不执行、不思考）
          if (cfg.blockQq) { console.log('[QuickCmd] QQ 屏蔽，吞掉消息'); return true; }
        } else {
          if (cfg.blockGame) { console.log('[QuickCmd] 游戏屏蔽，吞掉消息'); return true; }
        }

        var cmd = String(hit.command || '').trim();
        if (!cmd || !bot) return false;

        // 命令始终发往游戏执行：临时让 QQ 桥接放行（避免非 / 开头的内容被转到 QQ）
        var savedCh = global.__bakabotQQ;
        global.__bakabotQQ = null;
        try { bot.chat(cmd); } catch (e) { console.log('[QuickCmd] 执行失败: ' + e.message); return false; }
        finally { global.__bakabotQQ = savedCh; }

        console.log('[QuickCmd] 关键词【' + hit.keyword + '】→ 执行 ' + cmd);

        // 回执到触发渠道：游戏内回执可关闭，QQ 照常
        var reply = '已执行 ' + cmd;
        try {
          if (ch) {
            if (typeof global.__bakabotQQOut === 'function') global.__bakabotQQOut(reply, ch);
          } else if (!cfg.suppressGameReply) {
            require('./utils/chat').sendToOwner(bot, reply);
          }
        } catch (e) {}
        return true;
      } catch (e) { return false; }
    }

    // 包装 handleMessage：命中关键词直接执行命令，不进 AI
    if (typeof handleMessage === 'function') {
      var __origHandleMessage = handleMessage;
      handleMessage = async function (username, message) {
        if (__runQuickCommand(username, message)) return;
        return __origHandleMessage.call(this, username, message);
      };
    }
  } catch(e) {}
})();
// ===== END Bakabot 命令提示词补丁 =====
";
        return patchCode + content;
    }

    private string PatchQQBridgeV2(string content)
    {
        // 先剥离旧版 QQ 桥接补丁（与初版共用标记），Repatch 时保证重新注入最新版
        content = Regex.Replace(content,
            @"\r?\n?// ===== (?:Bakabot|ARCbot) QQ 桥接补丁 __(?:bakabot|arcbot)_qq_bridge__ =====[\s\S]*?// ===== END (?:Bakabot|ARCbot) QQ 桥接补丁 =====\r?\n?", "");

        if (content.Contains("__bakabot_qq_bridge__"))
            return content;

        var patchCode = @"
// ===== Bakabot QQ 桥接补丁 __bakabot_qq_bridge__ =====
(function(){
  try {
    if (global.__bakabotQQBridgePatched) return;
    global.__bakabotQQBridgePatched = true;

    // 当前会话通道：{ active, qq, groupId, player }；游戏内消息到来时清空切回游戏
    global.__bakabotQQ = null;
    // 当前指令的回复通道兜底：处理期间即使被杂项事件误切通道，回复仍发回 QQ
    global.__bakabotQQReply = null;
    global.__bakabotQQBot = null;
    global.__bakabotQQReady = false;
    // 按 QQ 号分开记会话历史（每人一份，最多 10 条）
    global.__bakabotQQHist = {};

    function __qqOut(text, ch) {
      try {
        ch = ch || global.__bakabotQQ;
        console.log('[QQ-OUT] ' + JSON.stringify({
          type: 'msg',
          qq: (ch && ch.qq) || '',
          groupId: (ch && ch.groupId) || '',
          text: String(text == null ? '' : text)
        }));
      } catch(e) {}
    }
    // 暴露给其他补丁（如命令提示词）发送 QQ 回复用
    global.__bakabotQQOut = __qqOut;

    // ── 拦截控制台输入：QQCMD JSON 行 → QQ 指令管道（绝不 bot.chat 发公屏）──
    var __origCreateInterface = require('readline').createInterface;
    require('readline').createInterface = function(opts) {
      var __rl = __origCreateInterface.apply(this, arguments);
      try {
        var __origOn = __rl.on.bind(__rl);
        __rl.on = function(event, listener) {
          if (event === 'line') {
            var __origListener = listener;
            listener = function(line) {
              var t = String(line || '').trim();
              if (t.indexOf('QQCMD ') === 0) { __handleQQCmd(t); return; }
              return __origListener.apply(this, arguments);
            };
          }
          return __origOn(event, listener);
        };
      } catch(e) {}
      return __rl;
    };

    function __handleQQCmd(line) {
      var msg = null;
      try { msg = JSON.parse(line.slice('QQCMD '.length)); } catch(e) {}
      if (!msg || !msg.text) return;
      var qq = String(msg.qq || '');
      var groupId = msg.groupId ? String(msg.groupId) : '';
      var player = String(msg.player || '').trim();
      if (!qq) return;
      global.__bakabotQQ = { active: true, qq: qq, groupId: groupId, player: player || null };
      // stop 类指令顺带清掉该 QQ 的会话历史（基座同时会清全局历史）
      if (String(msg.text).trim() === 'stop') global.__bakabotQQHist[qq] = [];
      __deliverQQ(String(msg.text), player || qq);
    }

    // ── 直接送入基座统一处理链（handleMessage 为主人判定 + LLM 规划）──
    function __deliverQQ(text, speaker) {
      try {
        var bot = global.__bakabotQQBot;
        if (!bot || !bot.entity) { __qqOut('机器人尚未登录游戏，暂时无法执行指令，请稍后再试。'); return; }
        if (!global.__bakabotQQReady) { __qqOut('机器人正在准备中，请稍后再试。'); return; }
        __wrapQqLlm();
        // 说话人切换为 QQ 绑定玩家（或 QQ 号），handleMessage 的主人检查据此放行；
        // 保持到下一条件指令（QQ 或游戏内），与聊天触发补丁的“服务对象切换”一致
        process.env.MC_OWNER_NAME = speaker;
        console.log('[QQ] 收到指令来自 ' + speaker + ': ' + text);
        // 兜底记录本次指令的回复通道，防止处理期间被系统消息等杂项事件切走
        global.__bakabotQQReply = global.__bakabotQQ;
        var __p = handleMessage(speaker, text);
        if (__p && typeof __p.catch === 'function') __p.catch(function(e){ console.log('[QQ] 指令处理异常: ' + (e && e.message)); });
      } catch(e) {
        console.log('[QQ] 指令处理失败: ' + (e && e.message));
      }
    }

    // ── 源头拦截：所有回复/行动消息最终都走 utils/chat 的 sendToOwner，
    //    在模块出口直接替换（不依赖 createBot 包装是否生效）──
    try {
      var __chatUtils = require('./utils/chat');
      var __origSTO = __chatUtils.sendToOwner;
      __chatUtils.sendToOwner = function(bot, message) {
        var s = String(message == null ? '' : message);
        var ch = global.__bakabotQQ && global.__bakabotQQ.active
          ? global.__bakabotQQ
          : (global.__bakabotQQReply || null);
        if (ch && ch.active) {
          if (s.indexOf('/tell ') === 0 || s.indexOf('/msg ') === 0) {
            var parts = s.split(' ');
            if (parts.length > 2) { parts.splice(0, 2); var txt = parts.join(' ').trim(); if (txt) __qqOut(txt, ch); }
            return;
          }
          if (s.charAt(0) !== '/') { __qqOut(s, ch); return; }
        }
        return __origSTO.apply(this, arguments);
      };
    } catch(e) {}

    // ── 把基座全局 chatHistory 的写入按当前通道分流（QQ → 各自历史，游戏 → 全局）──
    function __installQQHistoryHook(history) {
      if (!history || history.__bakabotQQHistHook) return;
      try {
        Object.defineProperty(history, '__bakabotQQHistHook', { value: true, configurable: true });
        var __origPush = history.push;
        history.push = function() {
          var ch = global.__bakabotQQ;
          if (ch && ch.active) {
            var arr = global.__bakabotQQHist[ch.qq] = global.__bakabotQQHist[ch.qq] || [];
            var n = Array.prototype.push.apply(arr, arguments);
            if (arr.length > 10) arr.shift();
            return n;
          }
          return __origPush.apply(this, arguments);
        };
      } catch(e) {}
    }

    // ── LLM 上下文：当前对话对象（QQ 用户）+ 未绑定提示 + 按 QQ 换历史 ──
    function __wrapQqLlm() {
      try {
        var __llm = require('./services/LLMService');
        if (!__llm || typeof __llm.generatePlan !== 'function' || __llm.__bakabotQQGenPatched) return;
        __llm.__bakabotQQGenPatched = true;
        var __origGen = __llm.generatePlan;
        __llm.generatePlan = function(sysPrompt, history, userPrompt) {
          try {
            var ch = global.__bakabotQQ;
            if (ch && ch.active) {
              __installQQHistoryHook(history);
              var hist = global.__bakabotQQHist[ch.qq] = global.__bakabotQQHist[ch.qq] || [];
              if (ch.player) {
                sysPrompt = String(sysPrompt) + '\n\n# 当前对话对象（QQ 用户）\n当前指令来自 QQ 用户「' + ch.player + '」（已绑定游戏 ID）。对方话语中的「我」等称呼均指「' + ch.player + '」；所有需要指定玩家的动作（TeleportRequest 的 target、FollowPlayer 的 player_name、DropItemAction 的 target_player 等）必须填写「' + ch.player + '」。';
              } else {
                sysPrompt = String(sysPrompt) + '\n\n# 当前对话对象（QQ 用户，未绑定）\n当前指令来自未绑定游戏 ID 的 QQ 用户。若指令需要指定玩家才能执行（如传送、跟随、丢物品给玩家），请直接回复：你还没有绑定游戏玩家，无法执行该指令。不要编造玩家名。';
              }
              sysPrompt = String(sysPrompt) + '\n\n# QQ 回复规则\n你的回复会通过 QQ 发送给对方。除非用户明确要求“在游戏内说话/发消息”，否则不要使用 Command 动作在游戏内发聊天消息；直接放在回复里即可。';
              return __origGen.call(this, sysPrompt, hist, userPrompt);
            }
          } catch(e) {}
          return __origGen.call(this, sysPrompt, history, userPrompt);
        };
      } catch(e) {}
    }

    // ── 包装 createBot：记录 bot、监听 spawn 就绪、输出分流、游戏消息切回游戏通道 ──
    var __mf = require('mineflayer');
    if (!__mf.__bakabotQQCreatePatched) {
      __mf.__bakabotQQCreatePatched = true;
      var __origCreateBot = __mf.createBot;
      __mf.createBot = function() {
        var __bot = __origCreateBot.apply(this, arguments);
        global.__bakabotQQBot = __bot;
        try { __bot.once('spawn', function(){ global.__bakabotQQReady = true; }); } catch(e) {}

        // 输出分流：QQ 通道中，发给玩家的回复（/tell /msg）与普通聊天播报 → [QQ-OUT]；开头命令仍进游戏
        try {
          var __origChat = __bot.chat.bind(__bot);
          __bot.chat = function(message) {
            var s = String(message == null ? '' : message);
            var ch = global.__bakabotQQ && global.__bakabotQQ.active
              ? global.__bakabotQQ
              : (global.__bakabotQQReply || null);
            if (ch && ch.active) {
              if (s.indexOf('/tell ') === 0 || s.indexOf('/msg ') === 0) {
                var parts = s.split(' ');
                if (parts.length > 2) { parts.splice(0, 2); var txt = parts.join(' ').trim(); if (txt) __qqOut(txt); }
                return;
              }
              if (s.charAt(0) !== '/') { __qqOut(s); return; }
            }
            return __origChat(s);
          };
        } catch(e) {}

        // 游戏内聊天/私信事件到达 → 会话通道切回游戏
        try {
          var __origEmit = __bot.emit;
          __bot.emit = function(eventName) {
            var ch = global.__bakabotQQ;
            if (ch && ch.active && (eventName === 'chat' || eventName === 'messagestr')) {
              var args = Array.prototype.slice.call(arguments, 1);
              var isSelf = false;
              var isCommand = false;
              if (eventName === 'chat') {
                // 公屏聊天：别人发言算游戏指令；机器人自己的回显不算
                var who = args[0];
                isSelf = who && __bot.username && String(who).toLowerCase() === String(__bot.username).toLowerCase();
                isCommand = !isSelf && !!who;
              } else {
                var s = String(args[0] || '');
                // 只把“玩家发来的私信”当作游戏指令切回游戏通道；
                // 系统/行动反馈等普通 messagestr（传送反馈、公告等）不再切走 QQ 回复通道，
                // 避免 QQ 指令处理中途被服务器消息打断后，回复发到游戏私聊
                if (/whispers to you:|悄悄地对你说/.test(s)) {
                  isCommand = true;
                } else {
                  var m = s.match(/^\[([a-zA-Z0-9_]+)\s*(?:[\u2190-\u21FF\u2794-\u27BE]|->|~)\s*[^\]]*\]/);
                  if (m) {
                    isSelf = __bot.username && String(m[1]).toLowerCase() === String(__bot.username).toLowerCase();
                    isCommand = !isSelf;
                  }
                }
              }
              if (isCommand) {
                // 真正的游戏指令到来：切回游戏通道，同时清掉 QQ 回复兜底
                global.__bakabotQQ = null;
                global.__bakabotQQReply = null;
              }
            }
            return __origEmit.apply(__bot, arguments);
          };
        } catch(e) {}
        return __bot;
      };
    }
  } catch(e) {}
})();
// ===== END Bakabot QQ 桥接补丁 =====
";
        return patchCode + content;
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
