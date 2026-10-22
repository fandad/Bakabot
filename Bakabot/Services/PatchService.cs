using System.Diagnostics;
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
        content = PatchOwnerKeywordMode(content);   // 第 4 种触发模式（主人+关键词）
        content = PatchMsgTellMode(content);
        content = PatchQQBridgeV2(content);
        content = PatchTpSplitMount(content);
        content = PatchQuickCommands(content);
        content = PatchStartupGreeting(content);
        content = PatchCreateBotOptions(content);

        // 取物提示词：告知模型附魔书要传附魔名（否则只会传 enchanted_book，无法区分是哪本）
        // 剥离时必须只去掉"追加的那一段"，不能删整行——提示词是接在锚点句尾的，
        // 删整行会把锚点句子本身也删掉，导致下次再也注入不回去（这里踩过一次坑）
        content = StripTakeItemsPrompt(content);
        if (content.Contains(TakeItemsPromptAnchor))
            content = content.Replace(TakeItemsPromptAnchor, TakeItemsPromptAnchor + TakeItemsPromptSuffix);

        if (content != original)
        {
            var backupPath = indexJs + ".bak";
            if (!File.Exists(backupPath))
                File.Copy(indexJs, backupPath, overwrite: true);

            File.WriteAllText(indexJs, content, Encoding.UTF8);
            // 写盘后立刻语法自检：注入逻辑出错时（曾把 const isOwner 注入成两份 →
            // SyntaxError: Identifier 'xxx' has already been declared）必须当场报出来，
            // 不能等到用户启动实例才发现机器人起不来
            VerifyJsSyntax(indexJs);
        }

        // 行动文件（src/actions、plugins）的公屏播报门禁（由 SUPPRESS_ACTION_CHAT 开关控制）
        PatchActionChatGate(srcDir);

        // 容器取物读取健壮性（等待 + 重试，避免误判容器为空）
        PatchTakeItems(srcDir);

        // 任务连贯性诊断（CraftItem / DropItemAction 关键节点打印真实背包）
        PatchTaskContinuityDiag(srcDir);

        // 步骤间验真（CraftItem 产出校验 + DropItem 校验 + StateMachine 下一步前置条件校验）
        PatchStepContinuity(srcDir);

        // 协议解析错误洪泛修复（26.2 服务器粒子包读不了时不再刷爆日志/猝死）
        PatchParseErrorFlood(srcDir);

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
        content = PatchOwnerKeywordMode(content);   // 第 4 种触发模式（主人+关键词）
        content = PatchMsgTellMode(content);
        content = PatchQQBridgeV2(content);
        content = PatchTpSplitMount(content);
        content = PatchQuickCommands(content);
        content = PatchStartupGreeting(content);
        content = PatchCreateBotOptions(content);

        // 取物提示词同步刷新（先剥离追加段再重新追加，保证幂等）
        content = StripTakeItemsPrompt(content);
        if (content.Contains(TakeItemsPromptAnchor))
            content = content.Replace(TakeItemsPromptAnchor, TakeItemsPromptAnchor + TakeItemsPromptSuffix);

        File.WriteAllText(indexJs, content, Encoding.UTF8);
        // 写盘后立刻语法自检（同 PatchInstance：注入错漏必须当场暴露，别等用户启动才发现）
        VerifyJsSyntax(indexJs);

        // 行动文件的公屏播报门禁同步刷新（内部自行先剥离旧注入再重新应用）
        PatchActionChatGate(srcDir);

        // 容器取物补丁同步刷新（幂等：先剥离本补丁各处注入，再无条件重新注入）
        PatchTakeItems(srcDir);

        // 任务连贯性诊断同步刷新（幂等：先还原上游原样，再重新注入）
        PatchTaskContinuityDiag(srcDir);

        // 步骤间验真同步刷新（幂等）
        PatchStepContinuity(srcDir);

        // 协议解析错误洪泛修复同步刷新（幂等）
        PatchParseErrorFlood(srcDir);

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
            ExtractZipSafe(zipPath, tmp);
    
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
    
    /// <summary>
    /// 安全解压 zip：逐条解压并允许覆盖。
    ///
    /// 不用 ZipFile.ExtractToDirectory(path, dir) 的默认重载——那不是 overwrite 模式，
    /// 一旦 zip 里存在**重名条目**（实测基础包里 minecraft-data\data\dataPaths.json 就重复），
    /// 会直接抛 IOException("...already exists") 使整段同步中断。
    ///
    /// 另外有的 zip 用反斜杠当分隔符（Windows 打的包），这里统一规范化，
    /// 否则 Path.Combine 出来会变成文件名里带反斜杠的畸形路径。
    /// </summary>
    private static void ExtractZipSafe(string zipPath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            // 目录条目（以 / 或 \ 结尾，或名字为空）
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var rel = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (rel.Length == 0) continue;

            var dest = Path.Combine(destDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            try
            {
                // 重名条目以"后出现的为准"，与 overwrite 语义一致
                entry.ExtractToFile(dest, overwrite: true);
            }
            catch
            {
                // 单个条目失败（加密、损坏等）不拖垮整段同步
            }
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
        // ⚠️ 先剥离本函数此前注入的所有内容，再无条件重新注入。
        //    这一步是必须的：RepatchInstance 会先把「聊天触发补丁块」整块删掉，
        //    而本函数对 index.js 正文的这几处改动**不在那个块里**（在 handleMessage 里、
        //    以及模块顶部），所以块被删后旧注入会残留；若不先剥离，下一次注入就会**叠加**，
        //    实测把 `const isOwner` 注入成两份 → SyntaxError（机器人起不来）。
        //
        //    顺序很讲究：必须**先把已注入的判定行还原成基础包原文**，让后面那段
        //    「找到原文 → 替换」的正常路径来处理它。若直接把这行删掉，就再也没有可匹配的
        //    原文了，反而会抛「既没有原文也没有标记」而卡死（这一步踩过两次）。
        const string bcBaseOwnerLine =
            "const isOwner = username.includes(process.env.MC_OWNER_NAME) || process.env.MC_OWNER_NAME.includes(username);";
        // 还原本补丁的三行注入块 → 基础包原文。
        // ⚠️ 必须用 [^\r\n]* 把**整行（含行尾注释）**吃掉，否则注释残留会让下一行匹配不上，
        //    结果留下孤儿 `const isOwner` → 重复声明（这里踩过）。
        content = Regex.Replace(content,
            @"[ \t]*const __bcOwnerNames = [^\r\n]*\r?\n[ \t]*__bcOwnerNames\.push\('__bakabot_qq_user__'\);[^\r\n]*\r?\n[ \t]*const isOwner = [^\r\n]*\r?\n?",
            "    " + bcBaseOwnerLine + "\r\n");
        // 模块顶部缓存的原始名单行、以及自检闸门行：直接删（它们会被无条件重新注入）
        content = Regex.Replace(content, @"[^\r\n]*global\.__bcQQOwners = [^\r\n]*\r?\n?", "");
        content = content.Replace(
            "if (username === bot.username && !global.__bakabotQQDispatching) return; // __bakabot_qq_selfcheck__",
            "if (username === bot.username) return;");
        content = Regex.Replace(content, @"[^\r\n]*__bakabot_qq_selfcheck__[^\r\n]*\r?\n?", "");

        // ── 0) QQ 转达的指令：修掉「绑定名撞机器人游戏名 → 被静默丢弃」──
        //
        // 背景（2026-10-02 实测）：基础包 handleMessage 第一道门是
        //     if (username === bot.username) return;      // 忽略机器人自己发的消息
        // 而 QQ 桥接是把**白名单里的绑定玩家名**当说话人传进来的。于是当某个 QQ 号绑定的
        // PlayerName 恰好等于机器人自己的游戏名时，该号的所有 QQ 指令都被这句自检丢掉：
        // 日志里只有 [QQ] 收到指令来自 X 与 [Debug] 收到消息来自 X，之后什么都没有、也不报错。
        //
        // 修法（两层，互补）：
        // ① **说话人改用不会撞名的内部标记**（见下面 __BC_QQ_MARK 的注释）——
        //    从根上消除名字碰撞，绑定名写成什么都不影响；
        // ② 自检处补一个「本次确实是 QQ 派发中」的闸门，双保险。
        //    该闸门由 QQ 桥接在 __deliverQQ 里同步置位；handleMessage 从入口到这句自检
        //    之间**没有任何 await**，所以 async 消息监听器不会在窗口内插进来。
        const string bcSelfCheck = "if (username === bot.username) return;";
        if (content.Contains(bcSelfCheck))
        {
            content = content.Replace(bcSelfCheck,
                "if (username === bot.username && !global.__bakabotQQDispatching) return; // __bakabot_qq_selfcheck__");
        }

        // ── 0b) 主人名单「源」：必须在被本补丁规范化之前先抓一份 ──
        //
        // ⚠️ 这里踩过一个真坑（2026-10-02）：PatchChatTrigger 底部会调用
        //     __normalizeOwnerEnv() → process.env.MC_OWNER_NAME = 名单里的【第一个】主人，
        //    因为基础包只认单一主人名。于是 handleMessage 里读 process.env.MC_OWNER_NAME
        //    时，**多主人名单已经被压成第一个了**。
        //    旧的「双向子串」比较碰巧还能放过其余主人（如 "Cold_" 命中 "Cold_Fan"），
        //    换成精确匹配后就暴露了：除第一位主人外，**其余主人全被误拒**（实测
        //    owners=["cold_fan"]、Yan_wan/w0nderfu_11 都进不去 LLM）。
        // 修法：在补丁最顶部（早于 __normalizeOwnerEnv）把原始名单缓存到一个全局里，
        //       主人判定优先用它。
        const string bcOwnerSrcLine = "(function () { try { global.__bcQQOwners = (process.env.MC_OWNER_NAME || '').split(',').map(function (s) { return s.trim().toLowerCase(); }).filter(Boolean); } catch (e) { global.__bcQQOwners = []; } })(); // __bakabot_owner_exact__";
        {
            // 目标：在【模块最顶部】抓一份原始主人名单，早于 PatchChatTrigger 自己的
            // __normalizeOwnerEnv()（它会把 MC_OWNER_NAME 压成第一位主人，导致其余主人被误拒）。
            // ⚠️ 不能用聊天触发补丁块做锚点：RepatchInstance 会先把这个块整块剥离再重新注入，
            //    所以本函数拿到的 content 里根本没有 __state / __bakabot_chat_trigger__（实测
            //    chat_trigger=False），用那个锚点会一直抛错。
            // 剥离已在函数开头统一做过，这里直接 prepend，天然幂等。
            content = bcOwnerSrcLine + "\n" + content;
        }

        // ── 1) 主人判定：把「子串比较」换成「按逗号名单精确匹配」──
        //
        // 基础包原文（index.js）：
        //   const isOwner = username.includes(process.env.MC_OWNER_NAME) || process.env.MC_OWNER_NAME.includes(username);
        // 这是**双向子串**比较，多主人名单下会误判，实测：
        //   · 名单「Cold_Fan,OoTomorioO,Yan_wan,w0nderfu_11」时，「0nderfu」/「Cold」/「dada_F」
        //     这类**部分名**会被判成主人；
        //   · 单主人「Cold_」时，「Cold_Fan」（另一个人）也会被判成主人。
        // 而基础包自己的 __isOwnerName() 一直是**精确**比较，说明精确才是本意，这里是历史缺陷。
        // 本补丁把该表达式换成等价语义的精确匹配（大小写不敏感 + 去空格），并把下面那个
        // 「QQ 转达」用的内部标记也一并算作可用主体（它就是本补丁自己注入的常量）。
        //
        // 幂等：基础里含有旧写法时才替换（RefreshPatches 若从 zip 重新同步了基础文件，
        // 旧写法会回来，这里会再替换一次）；已替换过的形态匹配不到，天然跳过。
        const string bcOldOwner =
            "const isOwner = username.includes(process.env.MC_OWNER_NAME) || process.env.MC_OWNER_NAME.includes(username);";
        // 注入后的正确形态（已经过上面的剥离，这里只会是新注入或基础包原样两种情形）
        const string bcPrepared = "global.__bcQQOwners.slice()";
        const string bcNewBlock =
            "const __bcOwnerNames = ((global.__bcQQOwners && global.__bcQQOwners.length) ? global.__bcQQOwners.slice()" +
            " : String(process.env.MC_OWNER_NAME || '').split(',').map((s) => s.trim().toLowerCase()).filter(Boolean)); // __bakabot_owner_exact__\r\n" +
            "    __bcOwnerNames.push('__bakabot_qq_user__'); // __bakabot_owner_exact__：QQ 转达指令的内部说话人标记，同样放行\r\n" +
            "    const isOwner = __bcOwnerNames.indexOf(String(username == null ? '' : username).trim().toLowerCase()) !== -1; // __bakabot_owner_exact__";

        if (content.Contains(bcOldOwner))
        {
            content = content.Replace(bcOldOwner, bcNewBlock);
        }
        else if (!content.Contains(bcPrepared))
        {
            // 既不是基础包原样、也还没注入过 → 属于预期外的形态，宁可报错也不静默跳过
            throw new InvalidOperationException(
                "PatchChatTrigger: 主人判定处既没有基础包原文、也没有本补丁注入标记，无法安全补丁（拒绝静默跳过）");
        }

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

    // ── 公屏兼容解析（TrChat / 其它""把玩家聊天发成系统消息""的插件服）──
    //
    // 背景（2026-10-21 查 TrChat v2 源码确认）：TrChat 在现代版本用
    //   player.sendSystemMessage(...) 发玩家聊天（NMSImpl12005 / NMSImpl260100），
    // 消息因此不是原版玩家聊天包，而是系统消息；而 mineflayer 的 chat 事件
    // 是**用一条正则去匹配渲染后的字符串**产生的（lib/plugins/chat.js）：
    //   ^(?:\(.{1,15}\)|\[.{1,15}\]|.){0,5}?(\w+)\s?[>:\-»\])~]+\s(.*)$
    // 于是格式稍有变化（全角冒号、等级标签、前缀层数过多、名字非 ASCII…）就会
    // 匹配失败（→ chat 不触发，消息全丢）或匹配到**错误的用户名**（→ 主人判定失败）。
    //
    // 策略：先用同一条正则判断""mineflayer 会怎么处理""：
    //   · 它匹配不上      → 聊天事件不会来，由本补丁解析并直接投递给基座；
    //   · 它匹配上了但用户名不是在线玩家 → 本补丁解析结果更可信，接管并屏蔽它那次；
    //   · 它匹配上了且用户名是在线玩家 → 放手让 mineflayer 处理（不重复、不干扰）。
    var __VANILLA_CHAT_RE = /^(?:\(.{1,15}\)|\[.{1,15}\]|.){0,5}?(\w+)\s?[>:\-»\])~]+\s(.*)$/;

    // 最近一次由""本补丁接管""投递的那行原文，用于屏蔽 mineflayer 紧随其后的 chat 事件
    var __lastOwnDispatch = { rendered: null, at: 0 };

    function __onlinePlayers() {
      var out = [];
      try {
        var bot = __state.bot;
        var pl = (bot && bot.players) || {};
        var me = bot && bot.username;
        for (var k in pl) {
          if (!Object.prototype.hasOwnProperty.call(pl, k)) continue;
          if (!k || k.length < 2) continue;
          if (me && k.toLowerCase() === String(me).toLowerCase()) continue;
          out.push(k);
        }
      } catch (e) { }
      return out;
    }

    function __isOnlinePlayer(name) {
      if (!name) return false;
      var ln = String(name).toLowerCase();
      var list = __onlinePlayers();
      for (var i = 0; i < list.length; i++) {
        if (list[i].toLowerCase() === ln) return true;
      }
      return false;
    }

    // 从 JSON 组件里找 clickEvent 指向 /tell <名字> 的名字。
    // TrChat 默认模板里玩家名自带「点击私聊」：suggest: '/tell %player_name% '，
    // 这是模板本身给出的机器可读信号，不受前缀/颜色/占位符影响。
    function __nameFromJson(jsonMsg) {
      try {
        var raw = null;
        if (jsonMsg) {
          if (typeof jsonMsg.json === 'object' && jsonMsg.json) raw = jsonMsg.json;
          else if (typeof jsonMsg.toJSON === 'function') raw = jsonMsg.toJSON();
          else if (typeof jsonMsg.toJson === 'function') raw = jsonMsg.toJson();
        }
        if (!raw) return null;
        var found = null;
        var walk = function (node) {
          if (!node || found) return;
          if (Array.isArray(node)) {
            for (var i = 0; i < node.length; i++) walk(node[i]);
            return;
          }
          if (typeof node !== 'object') return;
          var ce = node.clickEvent;
          if (ce && typeof ce === 'object') {
            var val = String(ce.value || '');
            var m = val.match(/^\/(?:tell|msg|m|w|whisper)\s+([A-Za-z0-9_]{2,16})\b/);
            if (m && __isOnlinePlayer(m[1])) { found = m[1]; return; }
          }
          if (node.extra) walk(node.extra);
          if (node.with) walk(node.with);
          if (node.children) walk(node.children);
        };
        walk(raw);
        return found;
      } catch (e) { return null; }
    }

    // 兜底：在渲染后的字符串里找「在线玩家名 + 分隔符 + 正文」。
    // 长名优先（避免 Cold_ 抢在 Cold_Fan 前面），并要求名字前面不是单词字符
    // （避免 ""of"" 命中 ""a lot of stuff: ..."" 这类误判）。
    // 名字后面允许跟一个短标签（等级/称号，如 [Lv.5] / (VIP)），分隔符允许连写（>> / || 等）。
    function __nameFromPlayers(str) {
      var s = String(str || '');
      var names = __onlinePlayers();
      if (!names.length) return null;
      names.sort(function (a, b) { return b.length - a.length; });
      var esc = function (x) { return String(x).replace(/[.*+?^${}()|[\]\\]/g, '\\$&'); };
      var re;
      try {
        re = new RegExp(
          '(^|[^A-Za-z0-9_])(' + names.map(esc).join('|') + ')' +
          '\\s*(?:\\[[^\\]]{0,20}\\]|\\([^)]{0,20}\\))?' +
          '\\s*[:：>»›|｜▍▎▏▕│┃]+\\s*([\\s\\S]+)', 'i');
      } catch (e) { return null; }
      var m = s.match(re);
      if (!m) return null;
      var text = String(m[3]).trim();
      if (!text) return null;
      return { speaker: m[2], msg: text };
    }

    // 综合解析：JSON clickEvent 优先，其次在线玩家名兜底
    function __parsePublicChat(str, jsonMsg) {
      var nm = __nameFromJson(jsonMsg);
      if (nm) {
        var cut = __sliceAfterName(str, nm);
        if (cut) return { speaker: nm, msg: cut };
      }
      return __nameFromPlayers(str);
    }

    // 从 ""前缀 + 名字 + 分隔符 + 正文"" 里切出正文。
    // 只在 JSON 已确认说话人时使用，所以可以宽容：名字之后跳过空白与常见分隔符
    // （冒号/尖括号/管道/箭头/装饰符），剩下的就是正文——服主改过分隔符也能切。
    function __sliceAfterName(str, name) {
      try {
        var s = String(str || '');
        var i = s.toLowerCase().indexOf(String(name).toLowerCase());
        if (i < 0) return null;
        var rest = s.slice(i + String(name).length);
        // 名字后允许一个短标签（[Lv.5] / (VIP)），再跳过""分隔符风格""的字符
        // （冒号/尖括号/管道/方块/短横/等号/间隔点），最多 6 个——避免把正文开头的标点也吃掉
        var m = rest.match(/^(?:\s*[\[(][^\])]{0,20}[\])])?[\s:：>»›|｜▍▎▏▕│┃\-–—=~·•]{0,6}([\s\S]*)$/);
        if (!m) return null;
        var text = String(m[1]).trim();
        return text || null;
      } catch (e) { return null; }
    }

    // ── 关键词连听（Follow-up）──
    // 触发一次后，同一个人接下来的若干条消息不必再说关键词。
    // · 只粘最近一次通过触发判定的人；新触发直接截断替换。
    // · 无关的人说话：既不打断也不扣次数（只有本人消息才扣）。
    // · 超时可关；开启时""滑动计时""——每收到一条被连听的消息就重新计时。
    // · 仅关键词类触发模式生效（owner_only 没有关键词概念）。
    var __followup = null;

    function __followupCfg() {
      if (String(process.env.CHAT_FOLLOWUP_ENABLED || 'false').toLowerCase() !== 'true') return null;
      var mode = String(process.env.TRIGGER_MODE || 'hybrid').toLowerCase();
      if (mode === 'owner_only') return null;
      var count = parseInt(process.env.CHAT_FOLLOWUP_COUNT || '3', 10);
      if (!isFinite(count) || count <= 0) return null;
      var timeoutEnabled = String(process.env.CHAT_FOLLOWUP_TIMEOUT_ENABLED || 'true').toLowerCase() === 'true';
      var timeoutSec = parseInt(process.env.CHAT_FOLLOWUP_TIMEOUT_SECONDS || '60', 10);
      if (!isFinite(timeoutSec) || timeoutSec <= 0) timeoutSec = 60;
      return { count: count, timeoutEnabled: timeoutEnabled, timeoutSec: timeoutSec };
    }

    function __followupExpired(st, cfg) {
      if (!st) return true;
      if (!cfg.timeoutEnabled) return false;
      return (Date.now() - st.at) > (cfg.timeoutSec * 1000);
    }

    // 本次被正常触发规则放行 → 开启/刷新连听窗口（截断：直接换成这个人）
    function __followupOpen(speaker, cfg) {
      __followup = {
        name: String(speaker == null ? '' : speaker).trim().toLowerCase(),
        remaining: cfg.count,
        at: Date.now()
      };
    }

    // 本次未被规则放行 → 看是否处于该玩家的连听窗口内；是则消费一次
    function __followupConsume(speaker, cfg) {
      if (!__followup) return false;
      if (__followupExpired(__followup, cfg)) { __followup = null; return false; }
      var ln = String(speaker == null ? '' : speaker).trim().toLowerCase();
      if (__followup.name !== ln) return false;
      if (__followup.remaining <= 0) { __followup = null; return false; }
      __followup.remaining -= 1;
      __followup.at = Date.now();
      if (__followup.remaining <= 0) __followup = null;
      return true;
    }

    // 判定消息处置方式：null(无关) / owner / speaker / block
    function __gate(eventName, args) {
      var speaker = null, msg = null;
      var dispatch = false;
      if (eventName === 'chat') {
        // mineflayer 由 messagestr 正则匹配产生 chat；若本补丁已接管同一行，
        // 这里必须拦下，否则一句话会被处理两遍（且这次的用户名可能是错的）
        if (__lastOwnDispatch.rendered !== null &&
            String(args[3]) === __lastOwnDispatch.rendered &&
            (Date.now() - __lastOwnDispatch.at) < 1500) {
          return { verdict: 'block', speaker: String(args[0] || '') };
        }
        speaker = args[0]; msg = args[1];
      }
      else if (eventName === 'messagestr') {
        var raw = String(args[0] == null ? '' : args[0]);
        var w = __parseWhisper(raw);
        if (w) { speaker = w.speaker; msg = w.msg; }
        else {
          var tellMode = String(process.env.TELL_MODE || 'whisper').toLowerCase();
          if (tellMode !== 'public') return null;
          var pos = args[1];
          if (pos === 'game_info' || pos === 2) return null;   // 动作栏/物品栏提示，不是聊天
          var vm = raw.match(__VANILLA_CHAT_RE);
          var parsed = null;
          if (!vm) {
            // mineflayer 匹配不上 → chat 事件永远不会来，必须自己投递
            parsed = __parsePublicChat(raw, args[2]);
          } else if (!__isOnlinePlayer(vm[1])) {
            // mineflayer 匹配上了但把非玩家（如世界名）当成了说话人 → 用更可信的结果接管
            var better = __parsePublicChat(raw, args[2]);
            if (better && __isOnlinePlayer(better.speaker)) parsed = better;
          }
          if (!parsed) return null;
          speaker = parsed.speaker; msg = parsed.msg;
          dispatch = true;
          __lastOwnDispatch.rendered = raw;
          __lastOwnDispatch.at = Date.now();
          if (String(process.env.DEBUG_MODE || '').toLowerCase() === 'true') {
            console.log('[Bakabot] 公屏兼容解析: ' + speaker + ' -> ' + msg);
          }
        }
      } else return null;
      if (!speaker) return null;
      var c = __cfg();
      if (c.owners.length === 0) return null;
      var owner = __isOwnerName(speaker, c.owners);
      var trig = __hasTrigger(msg, c.trigger);
      var allow = c.mode === 'owner_only' ? owner
                : c.mode === 'keyword_only' ? trig
                : (owner || trig);
      // 关键词连听：正常触发 → 刷新窗口；否则看是否在窗口内
      var fuCfg = null;
      try { fuCfg = __followupCfg(); } catch (e) { fuCfg = null; }
      if (allow) {
        if (fuCfg) { try { __followupOpen(speaker, fuCfg); } catch (e) { } }
      } else if (fuCfg) {
        try { if (__followupConsume(speaker, fuCfg)) allow = true; } catch (e) { }
      }
      if (!allow) return { verdict: 'block', speaker: speaker };
      __state.speaker = speaker;
      return { verdict: owner ? 'owner' : 'speaker', speaker: speaker, dispatch: dispatch, msg: msg };
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
            return Promise.resolve(__origGen.call(this, sysPrompt, history, userPrompt)).then(function(r) {
            // AI 回复已生成：标记下一条 QQ 输出为 AI 消息（启动器据此做“只回 AI”筛选）
            global.__bakabotQQAIMsg = true;
            return r;
          });
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
            // 由 messagestr 兜底解析出来的公屏消息：mineflayer 不会自己 emit chat，
            // 必须在这里直接投递给基座；仍继续向下传，保证其它 messagestr 监听器
            // （TPA 接受、死亡回传、自动登录等）照常工作——基座自己的公屏分支
            // 在 public 模式下会提前 return，因此不会重复处理。
            if (r.dispatch && typeof handleMessage === 'function') {
              try { handleMessage(r.speaker, r.msg); } catch (e) {}
            }
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
    /// 「主人关键词」触发模式补丁（第 4 种模式，注入 index.js 的聊天触发补丁块内）。
    ///
    /// 语义（用户 2026-10-02 确认）：**主人 AND 关键词**——即 owner_only 与 keyword_only 的叠加，
    /// 只有主人说了含关键词的消息才触发；他人说关键词不触发、主人不带关键词也不触发。
    /// 纯匹配规则，不引入任何"激活状态"，所以没有有效期/休眠词的概念
    /// （需要停时用现有的停止机制即可：主人说"停止/取消"由 LLM 走 stop，
    ///  或主人直接发精确的 stop，底层会 stopAll）。
    ///
    /// 注意：本模式必须配置 CHAT_TRIGGER，否则关键词恒为空、永远不会触发（UI 里已注明）。
    ///
    /// 幂等：两处都是精确字符串替换，已替换过的形态匹配不到即天然跳过。
    /// </summary>
    private static string PatchOwnerKeywordMode(string content)
    {
        const string marker = "__bakabot_owner_keyword__";

        // 1) 模式白名单：放行 owner_keyword（原逻辑会把不认识的值退回 hybrid）
        content = content.Replace(
            "if (mode !== 'owner_only' && mode !== 'keyword_only') mode = 'hybrid';",
            "if (mode !== 'owner_only' && mode !== 'keyword_only' && mode !== 'owner_keyword') mode = 'hybrid'; // " + marker);

        // 2) 放行判定：owner_keyword = 主人 && 关键词
        //
        // ⚠️ 必须用换行无关的正则：这段话是**多行**的，而文件是 CRLF；
        //    写字面 "\n" 的多行串**永远匹配不上**，于是白名单注入成功、判定这处静默失败，
        //    结果 owner_keyword 落进 `(owner || trig)` 分支 = **等同于 hybrid**——
        //    表现就是"主人说一次关键词之后不带关键词也回应"（2026-10-02 用户实测反馈）。
        //    这个坑在本项目已反复出现，多行匹配一律用 \r?\n 或按行定位。
        {
            var reAllow = new Regex(
                @"var allow = c\.mode === 'owner_only' \? owner\r?\n" +
                @"([ \t]*): c\.mode === 'keyword_only' \? trig\r?\n" +
                @"([ \t]*): \(owner \|\| trig\);");
            if (reAllow.IsMatch(content))
            {
                content = reAllow.Replace(content,
                    "var allow = c.mode === 'owner_only' ? owner\n" +
                    "$1: c.mode === 'keyword_only' ? trig\n" +
                    "$1: c.mode === 'owner_keyword' ? (owner && trig) // " + marker + "：主人且说了关键词\n" +
                    "$2: (owner || trig);", 1);
            }
            else if (!content.Contains("c.mode === 'owner_keyword' ? (owner && trig)"))
            {
                // 既没有已注入的形态、又没有可注入的锚点 → 必须报错，不能静默
                Console.WriteLine("[Patch] 警告：未找到 allow 判定锚点，owner_keyword 模式不会生效（会退化成 hybrid）");
            }
        }

        return content;
    }

    /// <summary>取物提示词补丁的锚点句（index.js 里 TakeItemsAction 说明中的一句）</summary>
    private const string TakeItemsPromptAnchor = "会按距离从近到远逐个打开容器寻找目标物品";

    /// <summary>追加在锚点句尾的附魔说明（含标记，便于剥离）</summary>
    private const string TakeItemsPromptSuffix =
        "；找附魔书时 item_names 直接填附魔名（如 \"mending\" / \"经验修补\" / \"锋利5\"），不要只填 enchanted_book，否则分不清是哪本 // __bakabot_takeitems_prompt__";

    /// <summary>
    /// 剥离"附加在锚点句尾"的取物提示词补充说明。
    /// 只删追加的那一段，保留锚点句本体——否则下次注入会失去锚点（踩过这个坑）。
    /// </summary>
    private static string StripTakeItemsPrompt(string content)
        => content.Replace(TakeItemsPromptAnchor + TakeItemsPromptSuffix, TakeItemsPromptAnchor);

    /// <summary>
    /// 容器取物补丁（注入 TakeItems.js，幂等，刷新补丁时自动同步）：
    ///
    /// 解决两个实测问题：
    ///   1) 容器内容偶尔读成空 → 原来只读一次就放弃并换下一个容器，没有任何等待或重试。
    ///      改为读到空则等 500ms 重读，最多 3 次；并打日志便于定位。
    ///   2) 附魔书这类靠 NBT 区分的物品认不出来 → 原来只按 item.name 精确匹配
    ///      （所有附魔书都叫 enchanted_book，无法区分"经验修补"），且 withdraw 没传 nbt。
    ///      改为支持附魔名匹配（'mending' / '经验修补' / '锋利5' / 'enchanted_book mending'），
    ///      并带上 item.nbt 取出正确的那一堆叠。
    ///      NBT 解析与附魔名映射沿用 TradeVillager 里已验证过的做法
    ///      （prismarine-nbt 的 simplify 对列表元素不可靠，故直接按原始 NBT 结构解析）。
    ///
    /// 幂等做法：先把本补丁注入的每一处都剥离干净（还原成上游原样），再无条件重新注入。
    /// </summary>
    private static void PatchTakeItems(string srcDir)
    {
        var file = Path.Combine(srcDir, "actions", "logic", "TakeItems.js");
        if (!File.Exists(file)) return;

        var content = File.ReadAllText(file, Encoding.UTF8);
        var original = content;
        const string marker = "__bakabot_takeitems_wait__";

        // 1) 剥离旧注入，保证刷新幂等：
        //    恢复成原始的 containerItems() / items() 表达式（两行都还原）
        content = Regex.Replace(content,
            @"\s*const containerItems = \(\(\) => \{[^\r\n]*" + marker + @"[\s\S]*?\}\)\(\);",
            "\n                    const containerItems = typeof chestWindow.containerItems === 'function'\n" +
            "                        ? chestWindow.containerItems()\n" +
            "                        : chestWindow.items();");

        // 1b) 还原"读取处"的调用形式
        content = content.Replace(
            "// " + marker + "：空结果会等待并重读，避免容器内容包未到就判定为空\n" +
            "                    const containerItems = await this._bakabotReadContainerItems(chestWindow); // " + marker,
            "const containerItems = typeof chestWindow.containerItems === 'function'\n" +
            "                        ? chestWindow.containerItems()\n" +
            "                        : chestWindow.items();");

        // 1c) 还原 withdraw 调用（去掉补丁加的 nbt 参数）
        content = content.Replace(
            "await chestWindow.withdraw(item.type, item.metadata, withdrawCount, item.nbt); // " + marker + "：带上 NBT 才能取对附魔书这类物品",
            "await chestWindow.withdraw(item.type, item.metadata, withdrawCount);");

        // 1d) 还原目标名过滤（去掉附魔匹配）——必须还原成上游**原样**，
        //     否则重新注入时匹配不到，会退化成"半补丁"状态（这里踩过一次坑）
        content = content.Replace(
            "return containerItems.filter(item => this.itemNames.some(n => this._bakabotItemMatches(item, n))); // " + marker,
            "return containerItems.filter(item => this.itemNames.includes(item.name));");

        // 1d2) 移除诊断日志块（还原成上游原样的空箱分支）
        // 用 .*? 吃掉中间所有内容，避免在 C# 字符串里书写含反引号的 JS 代码
        content = Regex.Replace(content,
            @"\r?\n[ \t]*// " + marker + @"：打印容器实况[\s\S]*?\r?\n[ \t]*console\.log\(.*?这个容器里没有目标物品.*?\);",
            "\n                        console.log(`[TakeItemsAction] 这个容器里没有目标物品，尝试下一个。`);");

        // 1d3) 任务连贯性诊断的"剥离"在 PatchTaskContinuityDiag 里做（那是另外两个文件），
        //      这里不处理，避免把不属于本文件的替换应用错对象。

        // 1e) 移除注入的 helper 方法块：用显式 START/END 边界，保证整块被吃掉
        content = Regex.Replace(content,
            @"[ \t]*// ===== __bakabot_takeitems_wait__ 辅助方法 START =====[\s\S]*?// ===== __bakabot_takeitems_wait__ 辅助方法 END =====[ \t]*\r?\n?",
            "");
        // 2) 注入（不设"是否已注入"的守卫：上面已把本补丁的每一处都剥离干净，
        //    所以这里对每一处都无条件重新注入；已注入形态匹配不到即天然跳过，保证幂等）
        {
            const string anchor = "_filterTargetItems(containerItems) {";
            if (content.Contains(anchor))
                content = content.Replace(anchor, "__BAKABOT_HELPER__\n\n    " + anchor);

            // 2b) 替换读取处，改用新的等待重试方法
            content = Regex.Replace(content,
                @"const containerItems = typeof chestWindow\.containerItems === 'function'\s*\r?\n\s*\? chestWindow\.containerItems\(\)\s*\r?\n\s*: chestWindow\.items\(\);",
                "// " + marker + "：空结果会等待并重读，避免容器内容包未到就判定为空\n" +
                "                    const containerItems = await this._bakabotReadContainerItems(chestWindow); // " + marker);

            // 2c) 取出时带上 NBT：附魔书等物品靠 NBT 区分，不带 nbt 会取错堆叠
            //     （参考 TradeVillager 的成熟做法；mineflayer 的 withdraw 第 4 参就是 nbt）
            content = content.Replace(
                "await chestWindow.withdraw(item.type, item.metadata, withdrawCount);",
                "await chestWindow.withdraw(item.type, item.metadata, withdrawCount, item.nbt); // " + marker + "：带上 NBT 才能取对附魔书这类物品");

            // 2d) 目标名匹配改为支持附魔（只按 name 精确匹配时，所有附魔书都叫 enchanted_book，无法区分）
            content = content.Replace(
                "return containerItems.filter(item => this.itemNames.includes(item.name));",
                "return containerItems.filter(item => this.itemNames.some(n => this._bakabotItemMatches(item, n))); // " + marker);

            // 2e) 匹配失败时打印诊断（DEBUG_MODE：容器里到底有什么、NBT 长什么样）
            //     只加日志、不改行为；排查完可留作长期诊断
            content = content.Replace(
                "if (targetItems.length === 0) {\n                        emptyCount++;\n                        console.log(`[TakeItemsAction] 这个容器里没有目标物品，尝试下一个。`);",
                "if (targetItems.length === 0) {\n                        emptyCount++;\n                        // " + marker + "：打印容器实况，便于定位\"认不出物品\"的原因\n" +
                "                        if (process.env.DEBUG_MODE === 'true') {\n" +
                "                            console.log(`[TakeItemsAction][诊断] 想要: ${JSON.stringify(this.itemNames)}`);\n" +
                "                            console.log(`[TakeItemsAction][诊断] 容器内 ${containerItems.length} 项: ` + containerItems.map(it => {\n" +
                "                                let en = '';\n" +
                "                                try { const e = this._bakabotEnchants(it); if (e) en = ' 附魔=' + JSON.stringify(e); } catch (_) {}\n" +
                "                                return `${it.name}(${it.displayName || ''})${en}`;\n" +
                "                            }).join(' | '));\n" +
                "                        }\n" +
                "                        console.log(`[TakeItemsAction] 这个容器里没有目标物品，尝试下一个。`);");
        }

        // 4) 填充 helper 实现（避免在 Replace 里写大段带引号代码）
        if (content.Contains("__BAKABOT_HELPER__"))
        {
            var helper =
                "// ===== " + marker + " 辅助方法 START =====\n" +
                "    // " + marker + "：带等待重试的容器读取（最多 3 次，空则 500ms 后重读）\n" +
                "    async _bakabotReadContainerItems(win) {\n" +
                "        const read = () => {\n" +
                "            try {\n" +
                "                return typeof win.containerItems === 'function' ? win.containerItems() : win.items();\n" +
                "            } catch (e) {\n" +
                "                if (process.env.DEBUG_MODE === 'true') console.log(`[TakeItemsAction] 读取容器内容出错: ${e.message}`);\n" +
                "                return [];\n" +
                "            }\n" +
                "        };\n" +
                "        let items = read();\n" +
                "        if (items && items.length > 0) return items;\n" +
                "        for (let attempt = 2; attempt <= 3; attempt++) {\n" +
                "            await this._sleep(500);\n" +
                "            const again = read();\n" +
                "            if (again && again.length > 0) {\n" +
                "                console.log(`[TakeItemsAction] 第 ${attempt} 次读取容器才拿到内容（共 ${again.length} 项），已按新内容处理`);\n" +
                "                return again;\n" +
                "            }\n" +
                "        }\n" +
                "        if (process.env.DEBUG_MODE === 'true') console.log(`[TakeItemsAction] 容器读 3 次仍为空（slotCount=${win && win.slots ? win.slots.length : '?'}）`);\n" +
                "        return items || [];\n" +
                "    }\n" +
                "\n" +
                "    // " + marker + "：附魔名 -> 官方 id 映射（含中文别名），惰性构建一次\n" +
                "    _bakabotEnchantMap() {\n" +
                "        if (this.__bakabotEnchMap) return this.__bakabotEnchMap;\n" +
                "        const map = { nameToId: {}, idToName: {} };\n" +
                "        try {\n" +
                "            const md = require('minecraft-data')(this.bot.version);\n" +
                "            const byName = md.enchantmentsByName || {};\n" +
                "            const byId = md.enchantmentsArray || md.enchantmentsById || md.enchantments || [];\n" +
                "            const cn = {\n" +
                "                '保护': 'protection', '火焰保护': 'fire_protection', '摔落保护': 'feather_falling',\n" +
                "                '爆炸保护': 'blast_protection', '弹射物保护': 'projectile_protection', '水下呼吸': 'respiration',\n" +
                "                '水下速掘': 'aqua_affinity', '荆棘': 'thorns', '深海探索者': 'depth_strider',\n" +
                "                '冰霜行者': 'frost_walker', '灵魂疾行': 'soul_speed', '锋利': 'sharpness',\n" +
                "                '亡灵杀手': 'smite', '节肢杀手': 'bane_of_arthropods', '击退': 'knockback',\n" +
                "                '火焰附加': 'fire_aspect', '抢夺': 'looting', '横扫之刃': 'sweeping',\n" +
                "                '效率': 'efficiency', '精准采集': 'silk_touch', '耐久': 'unbreaking',\n" +
                "                '时运': 'fortune', '力量': 'power', '冲击': 'punch', '火矢': 'flame',\n" +
                "                '无限': 'infinity', '海之眷顾': 'luck_of_the_sea', '饵钓': 'lure',\n" +
                "                '经验修补': 'mending', '消失诅咒': 'vanishing_curse', '绑定诅咒': 'binding_curse',\n" +
                "                '迅捷潜行': 'swift_sneak', '风之利刃': 'wind_burst', '密度': 'density',\n" +
                "                '破甲': 'breach', '引雷': 'channeling', '忠诚': 'loyalty', '穿刺': 'impaling',\n" +
                "                '激流': 'riptide', '多重射击': 'multishot', '穿透': 'piercing', '快速装填': 'quick_charge',\n" +
                "            };\n" +
                "            for (const [cnName, id] of Object.entries(cn)) map.nameToId[cnName] = id;\n" +
                "            for (const id of Object.keys(byName)) {\n" +
                "                const info = byName[id];\n" +
                "                if (!info) continue;\n" +
                "                map.nameToId[id] = id;\n" +
                "                map.idToName[info.id] = id;\n" +
                "                if (info.displayName) map.nameToId[String(info.displayName).toLowerCase()] = id;\n" +
                "            }\n" +
                "            if (Array.isArray(byId)) {\n" +
                "                for (const info of byId) { if (info && info.name) map.idToName[info.id] = info.name; }\n" +
                "            }\n" +
                "        } catch (_) { /* 拿不到映射就退化成只按物品名匹配 */ }\n" +
                "        this.__bakabotEnchMap = map;\n" +
                "        return map;\n" +
                "    }\n" +
                "\n" +
                "    // " + marker + "：解析请求里的附魔（'mending' / '经验修补' / '锋利5' / 'enchanted_book mending'）\n" +
                "    _bakabotParseEnchant(name) {\n" +
                "        if (!name) return null;\n" +
                "        let s = String(name).toLowerCase().replace(/\\s+/g, '');\n" +
                "        s = s.replace(/enchanted_book|附魔书|附魔/g, '');\n" +
                "        if (!s) return null;\n" +
                "        const map = this._bakabotEnchantMap();\n" +
                "        if (!map || !map.nameToId) return null;\n" +
                "        if (map.nameToId[s]) return { id: String(map.nameToId[s]), level: null };\n" +
                "        const m = s.match(/^([a-z_]+|[\\u4e00-\\u9fa5]+)(\\d+)$/);\n" +
                "        if (m && map.nameToId[m[1]]) return { id: String(map.nameToId[m[1]]), level: parseInt(m[2], 10) };\n" +
                "        return null;\n" +
                "    }\n" +
                "\n" +
                "    // " + marker + "：把附魔 id 统一成官方名字。\n" +
                "    // 组件里存的是**数字 id**（实测附魔书：{\"id\":64,\"level\":1}），\n" +
                "    // 数字 id 会随版本/协议转换变化，必须用 minecraft-data 的注册表翻回名字。\n" +
                "    _bakabotResolveEnchantId(id) {\n" +
                "        if (id == null) return null;\n" +
                "        if (typeof id === 'string') return id.toLowerCase().replace(/^minecraft:/, '');\n" +
                "        const map = this._bakabotEnchantMap();\n" +
                "        if (map && map.idToName && map.idToName[id]) return String(map.idToName[id]).toLowerCase().replace(/^minecraft:/, '');\n" +
                "        return null;\n" +
                "    }\n" +
                "\n" +
                "    // " + marker + "：从「物品组件」读附魔（1.20.5+ 的真实来源）\n" +
                "    //\n" +
                "    // 实测结构（1.21.4 附魔书，经 ViaProxy 从 26.2 翻译）：\n" +
                "    //   components = [ {\"type\":\"stored_enchantments\",\n" +
                "    //                   \"data\":{\"enchantments\":[{\"id\":64,\"level\":1}],\"showInTooltip\":true}} ]\n" +
                "    // 注意两点：\n" +
                "    //   1) 附魔书用 stored_enchantments，普通装备用 enchantments —— 两个都要认；\n" +
                "    //   2) prismarine-item 自带的 item.enchants getter 只查 'enchantments'，\n" +
                "    //      对附魔书（stored_enchantments）会返回空数组，所以不能只靠它。\n" +
                "    _bakabotEnchantsFromComponents(item) {\n" +
                "        try {\n" +
                "            if (!item) return null;\n" +
                "            const out = [];\n" +
                "            const take = (list) => {\n" +
                "                if (!Array.isArray(list)) return;\n" +
                "                for (const en of list) {\n" +
                "                    if (!en) continue;\n" +
                "                    const raw = en.id !== undefined ? en.id : en.name;\n" +
                "                    const name = this._bakabotResolveEnchantId(raw);\n" +
                "                    if (!name) continue;\n" +
                "                    let lvl = en.level !== undefined ? en.level : en.lvl;\n" +
                "                    if (typeof lvl !== 'number') lvl = parseInt(lvl, 10) || 1;\n" +
                "                    out.push({ id: name, lvl: lvl });\n" +
                "                }\n" +
                "            };\n" +
                "            // (a) components 数组（实测的形态）\n" +
                "            if (Array.isArray(item.components)) {\n" +
                "                for (const comp of item.components) {\n" +
                "                    if (!comp || (comp.type !== 'stored_enchantments' && comp.type !== 'enchantments')) continue;\n" +
                "                    take(comp.data && comp.data.enchantments);\n" +
                "                }\n" +
                "            }\n" +
                "            // (b) componentMap（库内部用的形态，兜底）\n" +
                "            if (out.length === 0 && item.componentMap && typeof item.componentMap.get === 'function') {\n" +
                "                for (const key of ['stored_enchantments', 'enchantments']) {\n" +
                "                    const c = item.componentMap.get(key);\n" +
                "                    if (c && c.data) take(c.data.enchantments);\n" +
                "                }\n" +
                "            }\n" +
                "            return out.length > 0 ? out : null;\n" +
                "        } catch (e) {\n" +
                "            if (process.env.DEBUG_MODE === 'true') console.log(`[TakeItemsAction] 读组件附魔失败: ${e.message}`);\n" +
                "            return null;\n" +
                "        }\n" +
                "    }\n" +
                "\n" +
                "    // " + marker + "：读取物品附魔（统一出口，按可靠性依次尝试）\n" +
                "    _bakabotEnchants(item) {\n" +
                "        if (!item) return null;\n" +
                "        // 1) 组件（1.20.5+ 的真实来源，优先）——能覆盖 item.enchants 漏掉的附魔书\n" +
                "        const fromComp = this._bakabotEnchantsFromComponents(item);\n" +
                "        if (fromComp) return fromComp;\n" +
                "        // 2) 库自带 getter（普通装备的 enchantments 组件、旧版 NBT，它内部会归一化）\n" +
                "        try {\n" +
                "            const list = item.enchants;\n" +
                "            if (Array.isArray(list) && list.length > 0) {\n" +
                "                const out = [];\n" +
                "                for (const en of list) {\n" +
                "                    if (!en) continue;\n" +
                "                    const name = this._bakabotResolveEnchantId(en.name !== undefined ? en.name : en.id);\n" +
                "                    if (!name) continue;\n" +
                "                    let lvl = en.lvl !== undefined ? en.lvl : en.level;\n" +
                "                    if (typeof lvl !== 'number') lvl = parseInt(lvl, 10) || 1;\n" +
                "                    out.push({ id: name, lvl: lvl });\n" +
                "                }\n" +
                "                if (out.length > 0) return out;\n" +
                "            }\n" +
                "        } catch (_) { /* 未知版本会抛错，交给下面的 NBT 兜底 */ }\n" +
                "        return null;\n" +
                "    }\n" +
                "\n" +
                "    // " + marker + "：读附魔书/装备的附魔（兼容旧 NBT 结构；组件交给 _bakabotEnchants）\n" +
                "    _bakabotStoredEnchants(item) {\n" +
                "        // 先走统一出口（覆盖组件与常规 NBT）\n" +
                "        const viaGetter = this._bakabotEnchants(item);\n" +
                "        if (viaGetter) return viaGetter;\n" +
                "        // 兜底：直接解析原始 NBT 列表（老版本或 getter 不支持的形态）\n" +
                "        try {\n" +
                "            if (!item || !item.nbt) return null;\n" +
                "            let root = item.nbt;\n" +
                "            while (root && (root.nbt || root.parsed) && !(root.value && (root.value.StoredEnchantments || root.value.Enchantments || root.value.ench))) {\n" +
                "                root = root.nbt || root.parsed;\n" +
                "            }\n" +
                "            const v = root && root.value;\n" +
                "            const stored = v && (v.StoredEnchantments || v.Enchantments || v.ench);\n" +
                "            if (!stored || !stored.value || !Array.isArray(stored.value.value)) return null;\n" +
                "            const map = this._bakabotEnchantMap();\n" +
                "            const out = [];\n" +
                "            for (const en of stored.value.value) {\n" +
                "                if (!en) continue;\n" +
                "                const body = (en.type === 'compound' && en.value) ? en.value : en;\n" +
                "                if (!body) continue;\n" +
                "                let id = body.id ? (body.id.value !== undefined ? body.id.value : body.id) : null;\n" +
                "                if (id == null) continue;\n" +
                "                if (typeof id === 'number' && map && map.idToName[id]) id = map.idToName[id];\n" +
                "                id = String(id).toLowerCase().replace(/^minecraft:/, '');\n" +
                "                let lvlTag = body.lvl !== undefined ? body.lvl : body.level;\n" +
                "                let lvl = lvlTag && lvlTag.value !== undefined ? lvlTag.value : lvlTag;\n" +
                "                if (typeof lvl !== 'number') lvl = parseInt(lvl, 10) || 1;\n" +
                "                out.push({ id: id, lvl: lvl });\n" +
                "            }\n" +
                "            return out.length > 0 ? out : null;\n" +
                "        } catch (_) { return null; }\n" +
                "    }\n" +
                "\n" +
                "    // " + marker + "：物品是否匹配请求名。支持三种写法：\n" +
                "    //   物品名本身（如 gunpowder / 火药）\n" +
                "    //   附魔名（如 mending / 经验修补）—— 匹配任意带该附魔的附魔书\n" +
                "    //   'enchanted_book mending' / '锋利5' —— 指定附魔与等级\n" +
                "    _bakabotItemMatches(item, name) {\n" +
                "        if (!item || !name) return false;\n" +
                "        const target = String(name).toLowerCase().trim();\n" +
                "        const itemName = String(item.name || '').toLowerCase();\n" +
                "        const itemDisplay = String(item.displayName || '').toLowerCase();\n" +
                "        const ench = this._bakabotParseEnchant(target);\n" +
                "        const plain = itemName === target || itemDisplay === target;\n" +
                "        if (!ench) return plain;\n" +
                "        // 请求的是附魔名（如 mending / 经验修补 / 锋利5）：按物品实际附魔匹配。\n" +
                "        // 不限制必须是 enchanted_book —— 带该附魔的任何物品都算命中，\n" +
                "        // 也避免因 name/displayName 取值差异而漏配。\n" +
                "        const got = this._bakabotStoredEnchants(item);\n" +
                "        if (got && got.length > 0) {\n" +
                "            return got.some(en => String(en.id) === ench.id && (ench.level == null || en.lvl === ench.level));\n" +
                "        }\n" +
                "        // 读不到附魔时退回按名匹配（例如请求写成 'enchanted_book mending'）\n" +
                "        return plain;\n" +
                "    }\n" +
                "\n" +
                "    // " + marker + "：任务连贯性诊断 —— 打印当前真实背包 + 当前窗口状态\n" +
                "    // 用途：定位\"上一步声称成功、下一步却拿不到东西\"（例如合成报成功但产物不在背包）。\n" +
                "    // 只打印、不改行为，且仅 DEBUG_MODE=true 时输出。\n" +
                "    async _bakabotDumpInv(tag) {\n" +
                "        if (process.env.DEBUG_MODE !== 'true') return;\n" +
                "        try {\n" +
                "            const inv = this.bot.inventory.items();\n" +
                "            const list = inv.map(i => `${i.name}x${i.count}`).join(', ') || '(空)';\n" +
                "            const held = this.bot.heldItem ? `${this.bot.heldItem.name}x${this.bot.heldItem.count}` : '无';\n" +
                "            const win = this.bot.currentWindow ? `${this.bot.currentWindow.type}(id=${this.bot.currentWindow.id})` : '无';\n" +
                "            console.log(`[连贯性诊断][${tag}] 背包=${list} | 手持=${held} | 当前窗口=${win}`);\n" +
                "        } catch (e) {\n" +
                "            console.log(`[连贯性诊断][${tag}] 打印背包失败: ${e.message}`);\n" +
                "        }\n" +
                "    }\n" +
                "    // ===== " + marker + " 辅助方法 END =====";
            content = content.Replace("__BAKABOT_HELPER__", helper);
        }

        // 5) 收敛空白：剥离/注入会留下一两个多余空行，多次刷新会逐轮累积。
        //    统一把 3 个以上连续换行压成 2 个（即最多留一个空行），使结果与刷新次数无关。
        content = Regex.Replace(content, @"(\r?\n){3,}", "\n\n");

        if (content != original)
        {
            File.WriteAllText(file, content, Encoding.UTF8);
            Console.WriteLine($"[Patch] TakeItems 容器取物补丁已注入: {file}");
        }
    }

    /// <summary>
    /// 任务连贯性诊断补丁（CraftItem.js / DropItemAction.js）。
    ///
    /// 背景（2026-10-02 实测）：三步任务"取圆石 → 合成熔炉 → 丢给主人"会散架——
    /// CraftItem 打印『成功合成 8 个 furnace』，但下一拍 DropItemAction 读到的背包是
    /// `cobblestonex64, breadx24`，既没有熔炉、圆石也没被消耗，说明"成功"是假的。
    /// 而 DropItemAction 在合成刚报错之后却能打印『成功丢弃了 8 个 furnace』，同样是假成功。
    ///
    /// 本补丁**只加诊断**（受 DEBUG_MODE 控制），不改任何行为，用来拿到"那一步之后背包到底长什么样"的实证，
    /// 再据此做真正的连贯性修复。打印用的 `_bakabotDumpInv` helper 由 PatchTakeItems 注入。
    ///
    /// 幂等：注入前先把上一轮的调用形式还原成上游原样，再无条件重新注入；
    /// 已还原/已注入两种形态各匹配其一，天然跳过。
    /// </summary>
    private static void PatchTaskContinuityDiag(string srcDir)
    {
        const string marker = "__bakabot_continuity__";
        const string startMark = "// ===== __bakabot_dumpinv__ 辅助方法 START =====";
        const string endMark = "// ===== __bakabot_dumpinv__ 辅助方法 END =====";

        // ⚠️ 注入位置必须用**唯一的长上下文**做锚点，不能用
        //    `if (!this.isExecuting) return;` 这种短句——它在 DropItemAction 里出现 5 次，
        //    无差别 Replace 会把 5 处全替换掉，还在同步函数里插 await 直接把语法搞坏。
        //
        // 本补丁在这个方法上累计踩过 5 次坑，都记在这里，改之前务必读：
        //   1) 把替换写进了 PatchTakeItems（那方法只读 TakeItems.js）→ 静默失效
        //   2) helper 只注入 TakeItems.js，另两个文件调用不到 → "is not a function"
        //   3) helper 插在 `this.bot = bot;` 之后 → 落进构造器内部，语法坏
        //   4) 剥离 helper 时吃掉空行 → 下次锚点匹配不上
        //   5) DropItemAction 的 dump 放进"背包没有该物品"失败分支 → 成功路径永不打印
        //   6) 用短锚点 `if (!this.isExecuting) return;` → 一次替换 5 处，语法坏 + 删掉原日志
        var craftFile = Path.Combine(srcDir, "actions", "craft", "CraftItem.js");
        var dropFile = Path.Combine(srcDir, "actions", "basic", "DropItemAction.js");

        // ---- CraftItem ----
        if (File.Exists(craftFile))
        {
            // ⚠️ 关键：先把整个文件规范成 **LF** 再动手。
            //    这套补丁栽在 CRLF 上已不止一次——文件本体是 CRLF，而 C# 里写字面 "\n" 的多行串
            //    永远匹配不上，剥离就静默失效、于是每次注入一份新的，最终 const 重复声明、
            //    机器人直接起不来（2026-10-02 14:00 的真实事故）。
            //    统一成 LF 后，下面所有 IndexOf / Replace 都是确定的；写盘也用 LF。
            var content = File.ReadAllText(craftFile, Encoding.UTF8).Replace("\r\n", "\n");
            var original = content;

            content = StripDumpHelper(content, startMark, endMark);

            // CraftItem 的两处锚点本身就是唯一字符串，直接双向替换即可
            const string craftOk = "console.log(`[CraftItem] 成功合成 ${this.count} 个 ${this.itemName}`);";
            const string craftOkPatch = "await this._bakabotDumpInv('合成成功之后');";
            const string craftErr = "console.error(`[CraftItem] 合成失败:`, err);";
            const string craftErrPatch = "await this._bakabotDumpInv('合成失败之后');";

            content = content.Replace(craftOkPatch, craftOk).Replace(craftOk, craftOkPatch);
            content = content.Replace(craftErrPatch, craftErr).Replace(craftErr, craftErrPatch);

            // ================= 任务连贯性修复（CraftItem）=================
            // 实测证据（2026-10-02 12:21）：CraftItem 报「成功合成 8 个 furnace」，
            // 2 秒后 DropItemAction 读到的背包却是 cobblestonex64, breadx24 —— 熔炉消失、圆石复原。
            // 即：合成在客户端成功，被服务器**回滚**了。而原实现只要 bot.craft 不抛异常就报成功，
            // 从不校验产物是否真在背包，于是假成功一路传给了下一步。
            //
            // 两处修复：
            //   ① 产出校验：合成后确认产物真的进了背包，否则如实报失败（craft_not_verified）
            //   ② 双窗口竞态：同一工作台被手工开一次、bot.craft 内部又开一次（日志 id=2/id=3），
            //      改为只让 bot.craft 开窗
            //
            // 实现方式：全程**按行**操作（文件已规范成 LF）。
            // 注入的每一行都带 marker，剥离时一句 RemoveAll 就够了——这是几轮踩坑后最稳的写法：
            //   · 只删第一处 -> 重复注入 -> const 重复声明 -> 机器人起不来
            //   · 把多行塞进一个列表元素 -> 下轮按行匹配不到 -> 留下孤儿 `}`
            {
                var lines = new List<string>(content.Split('\n'));

                // 逐行剥离本补丁注入（带 marker 的行全部删除，包括②那条 bot.craft）
                lines.RemoveAll(l => l.Contains(marker));

                // ---- 注入 ①：产出校验（基线放在 recipeToUse 之前）----
                var rIdx = lines.FindIndex(l => l.Contains("const recipeToUse = availableRecipes[0];"));
                if (rIdx >= 0)
                {
                    var ind = lines[rIdx][..(lines[rIdx].Length - lines[rIdx].TrimStart().Length)];
                    lines.Insert(rIdx, ind + "const __bakabotBefore = this._bakabotCountItem(this.itemName); // " + marker);
                }
                else Console.WriteLine($"[Patch] 警告：{craftFile} 未找到 recipeToUse 锚点");

                // ---- 注入 ①b：校验块（放在 _craftWithTable 调用之后）----
                var cIdx = lines.FindIndex(l => l.Contains("await this._craftWithTable(recipeToUse, this.count, tableBlock);"));
                if (cIdx >= 0)
                {
                    var ind = lines[cIdx][..(lines[cIdx].Length - lines[cIdx].TrimStart().Length)];
                    lines.InsertRange(cIdx + 1, new[]
                    {
                        ind + "const __bakabotAfter = this._bakabotCountItem(this.itemName); // " + marker,
                        ind + "const __bakabotGot = __bakabotAfter - __bakabotBefore; // " + marker,
                        ind + "if (__bakabotGot < this.count) { // " + marker,
                        ind + "    console.log('[CraftItem] 产出校验未通过 —— 期望 ' + this.count + ' 个，实际只多了 ' + __bakabotGot + ' 个（合成前 ' + __bakabotBefore + '，合成后 ' + __bakabotAfter + '）。多半是服务器回滚了这次合成。'); // " + marker,
                        ind + "    sendToOwner(this.bot, '⚠️ 合成【' + this.itemName + '】没有真正生效（期望 ' + this.count + ' 个，实际只多了 ' + __bakabotGot + ' 个），可能是服务器回滚了，我先停下。'); // " + marker,
                        ind + "    if (this.craftingWindow) { this.bot.closeWindow(this.craftingWindow); this.craftingWindow = null; } // " + marker,
                        ind + "    this.stop('craft_not_verified'); // " + marker,
                        ind + "    return; // " + marker,
                        ind + "} // " + marker
                    });
                }
                else Console.WriteLine($"[Patch] 警告：{craftFile} 未找到 _craftWithTable 调用锚点");

                content = string.Join("\n", lines);
            }
            // ⚠️ 顺序很重要：② 必须作用于 ① **join 之后**的 content，
            //    否则 ① 的 join 会把 ② 对 content 的修改覆盖掉（用旧变量覆盖新串，踩过）。

            // ---- 注入 ②：去掉手工开窗（交给 bot.craft 独占）----
            // 先做一次（作用于原始 content），跑完 ① 之后再补一次（作用于 join 后的 content），
            // 保证无论中间状态如何，最终都是"上游 4 行 → 单行带 marker"。
            var upstreamBlock =
                "        this.craftingWindow = await this._openCraftingTable(tableBlock);\n" +
                "        if (!this.craftingWindow) {\n" +
                "            throw new Error('无法打开工作台界面');\n" +
                "        }\n" +
                "\n" +
                "        await this.bot.craft(recipe, count, tableBlock);";
            var patchedLine = "        await this.bot.craft(recipe, count, tableBlock); // " + marker;
            // ② 必须作用于 ① join **之后**的 content（顺序坑：否则 ① 的 join 会用旧变量覆盖掉它）
            content = ApplyCraftWithTableFix(content, upstreamBlock, patchedLine);
            if (!content.Contains(patchedLine))
                Console.WriteLine($"[Patch] 警告：{craftFile} 双窗口修复未生效（_craftWithTable 锚点异常）");

            content = InjectDumpHelper(content, startMark, endMark);
            if (!content.Contains("async _bakabotDumpInv(tag)"))
                Console.WriteLine($"[Patch] 警告：{craftFile} 构造器锚点未命中，诊断 helper 未注入");

            content = Regex.Replace(content, @"(\r?\n){3,}", "\n\n");
            if (content != original)
            {
                File.WriteAllText(craftFile, content, Encoding.UTF8);
                Console.WriteLine($"[Patch] 任务连贯性诊断已注入: {craftFile}");
                VerifyJsSyntax(craftFile);
            }
        }

        // ---- DropItemAction ----
        if (File.Exists(dropFile))
        {
            // 同样先规范成 LF（理由见 CraftItem 处）
            var content = File.ReadAllText(dropFile, Encoding.UTF8).Replace("\r\n", "\n");
            var original = content;

            content = StripDumpHelper(content, startMark, endMark);

            // ⚠️ 这些文件是 **CRLF**（连基础包原文件也是），所以多行匹配绝不能写字面 "`\n"，
            //    必须用 \r?\n 的正则 —— 本补丁在这里栽过一次：单行锚点没问题，
            //    一写多行就永远匹配不上，白跑一轮实测。
            // 1) 失败分支：还原成上游原样（历史上这里被错塞过 dump 调用）
            content = Regex.Replace(content,
                @"([ \t]*sendToOwner\(this\.bot, `⚠️ 背包中没有【\$\{this\.itemName\}】，无法丢弃。`\);)[ \t]*\r?\n[ \t]*await this\._bakabotDumpInv\('丢完之后'\);[ \t]*\r?\n",
                "$1\r\n" +
                "                console.log(`[DropItemAction] 当前背包内容: ${this.bot.inventory.items().map(i => `${i.name}x${i.count}`).join(', ') || '(空)'}`);\r\n");

            // 2) 成功路径：在 toss 循环结束（droppedTotal 计算之前）打印真实背包，
            //    并**校验丢弃是否真的生效**（历史上出现过"报成功丢弃 8 个 furnace，实则背包里从来没有熔炉"）。
            //    刻意**不用正则**：`[ \t]*` 会贪婪跨过换行，导致看似正确的模式永远匹配不上；
            //    改成按行定位，确定性最强，也不受 CRLF/LF 影响。
            {
                // 先清掉上一轮的注入（按行、按标记），再重新插入。
                // ⚠️ 不要依赖 `if (!this.isExecuting) return;` 做锚点——它出现多次，
                //    且插入基线行后位置会漂移，导致第二轮报"未找到守卫"。
                //    这里改成**以 droppedTotal 行为唯一锚点**：基线插在它之前，校验插在它之前。
                var lines = new List<string>(content.Split('\n'));
                // 剥离：凡是带本补丁 marker 的行一律删掉。
                // ⚠️ 注入时必须**一行一个列表元素**，不能把多行塞进一个元素——
                //    否则下一轮按行 RemoveAll 匹配不到块内那些行，会留下孤儿 `}`，
                //    把文件搞成 "Missing catch or finally after try"（踩过）。
                lines.RemoveAll(l => l.Contains(marker));

                var idx = lines.FindIndex(l => l.Contains("const droppedTotal = "));
                if (idx > 0)
                {
                    var indent = lines[idx][..(lines[idx].Length - lines[idx].TrimStart().Length)];
                    var block = new List<string>
                    {
                        indent + "const __bakabotBeforeDrop = this._bakabotCountItem(resolvedName); // " + marker,
                        indent + "const __bakabotAfterDrop = this._bakabotCountItem(resolvedName); // " + marker,
                        indent + "const __bakabotActuallyDropped = __bakabotBeforeDrop - __bakabotAfterDrop; // " + marker,
                        indent + "await this._bakabotDumpInv('丢完之后'); // " + marker,
                        indent + "// 判据：数量没减少**且背包里还有该物品**才算失败。",
                        indent + "// 实测教训：丢弃成功后 after=0，若写成 `__bakabotActuallyDropped === 0` 会把",
                        indent + "// 每一次成功丢弃都误报成\"没生效\"（because before-after 也是 0）。",
                        indent + "if (__bakabotActuallyDropped <= 0 && __bakabotAfterDrop > 0) { // " + marker,
                        indent + "    console.log('[DropItemAction] 丢弃未生效 —— 报成功但背包里还有 ' + __bakabotAfterDrop + ' 个【' + resolvedName + '】，多半被服务器回滚。'); // " + marker,
                        indent + "    sendToOwner(this.bot, '⚠️ 丢弃【' + resolvedName + '】好像没有真正生效（背包里还有 ' + __bakabotAfterDrop + ' 个），可能是服务器回滚了。'); // " + marker,
                        indent + "    this.stop('drop_not_verified'); // " + marker,
                        indent + "    return; // " + marker,
                        indent + "} // " + marker
                    };
                    lines.InsertRange(idx, block);
                }
                else
                {
                    Console.WriteLine($"[Patch] 警告：{dropFile} 未找到 droppedTotal，丢弃后校验未注入");
                }
                content = string.Join("\n", lines);   // 保持 LF（见 CraftItem 处的说明）
            }

            content = InjectDumpHelper(content, startMark, endMark);
            if (!content.Contains("async _bakabotDumpInv(tag)"))
                Console.WriteLine($"[Patch] 警告：{dropFile} 构造器锚点未命中，诊断 helper 未注入");

            // 3) 兜底：清掉任何残留的重复 dump 调用（历史上被短锚点一次替换 5 处过）
            content = Regex.Replace(content,
                @"([ \t]*)await this\._bakabotDumpInv\('丢完之后'\);[ \t]*\r?\n(?=[ \t]*await this\._bakabotDumpInv\('丢完之后'\);)",
                "");

            content = Regex.Replace(content, @"(\r?\n){3,}", "\n\n");
            if (content != original)
            {
                File.WriteAllText(dropFile, content, Encoding.UTF8);
                Console.WriteLine($"[Patch] 任务连贯性诊断已注入: {dropFile}");
                VerifyJsSyntax(dropFile);
            }
        }
    }

    /// <summary>
    /// 步骤间「验真」补丁（StateMachine.js + ConditionParser.js）。
    ///
    /// 背景：这是用户反馈的「多步骤任务连贯性」问题的核心层。
    /// 现状是——每个任务只由它自己的 Action 自测成败，**从没有人检查上一步的产物对下一步是否真的可用**：
    ///   · 任务 2「合成8个熔炉」报成功（客户端成功、被服务器回滚）
    ///   · 任务 3「丢给主人」接手时背包里没有熔炉 → no_item_in_inventory
    /// 而 `SUPPRESS_ACTION_CHAT=true` 时失败还被静默吞掉，计划照常往下走，错误一路累积。
    ///
    /// 本补丁做两件事：
    ///   ① ConditionParser 支持 `has_items` —— 提示词里 LLM 输出的就是 `condition: { "has_items": {...} }`，
    ///      而原实现只认 `condition.items`，**导致先决条件检查一直静默失效**（格式错配）。
    ///   ② StateMachine 在某步成功、进入下一步之前，先校验下一步的前置条件是否真的满足；
    ///      不满足就打醒目日志 + 告知主人，并把后续任务交回计划重试（而不是继续硬跑）。
    /// </summary>
    private static void PatchStepContinuity(string srcDir)
    {
        const string marker = "__bakabot_step_continuity__";

        // ---- ① ConditionParser 支持 has_items ----
        var cpFile = Path.Combine(srcDir, "core", "ConditionParser.js");
        if (File.Exists(cpFile))
        {
            var cp = File.ReadAllText(cpFile, Encoding.UTF8);
            var cpOriginal = cp;

            // 幂等：先还原，再注入。
            // ⚠️ 用**按行定位**——这个文件是 CRLF，写字面 "\n" 的多行串永远匹配不上（本项目反复踩过）。
            {
                var lines = new List<string>(cp.Replace("\r\n", "\n").Split('\n'));
                // 移除上一轮注入（含它自己的收尾 `}`，该 `}` 带标记以区别于文件原有结构）
                lines.RemoveAll(l => l.Contains(marker) ||
                                      l.Trim() == "if (condition.has_items) {" ||
                                      l.Trim() == "this._validateItems(bot, condition.has_items, missing);");
                var anchorIdx = lines.FindIndex(l => l.Contains("// 1. 校验消耗品 (items)"));
                if (anchorIdx >= 0)
                {
                    var indent = lines[anchorIdx][..(lines[anchorIdx].Length - lines[anchorIdx].TrimStart().Length)];
                    lines.InsertRange(anchorIdx + 1, new[]
                    {
                        indent + "// " + marker + "：提示词里 LLM 输出的是 has_items，原实现只认 items，",
                        indent + "// 导致先决条件检查静默失效——两种写法都接受。",
                        indent + "if (condition.has_items) {",
                        indent + "    this._validateItems(bot, condition.has_items, missing);",
                        indent + "} // " + marker + "_has_items_end"
                    });
                }
                else
                {
                    Console.WriteLine($"[Patch] 警告：{cpFile} 未找到 items 校验锚点，has_items 支持未注入");
                }
                cp = string.Join("\n", lines);   // 保持 LF
            }

            if (cp != cpOriginal)
            {
                File.WriteAllText(cpFile, cp, Encoding.UTF8);
                Console.WriteLine($"[Patch] 步骤间验真(ConditionParser)已注入: {cpFile}");
                VerifyJsSyntax(cpFile);
            }
        }

        // ---- ② StateMachine 步骤间校验下一步前置条件 ----
        var smFile = Path.Combine(srcDir, "core", "StateMachine.js");
        if (File.Exists(smFile))
        {
            var sm = File.ReadAllText(smFile, Encoding.UTF8);
            var smOriginal = sm;

            // 幂等：先移除上一轮注入块（显式边界）
            sm = Regex.Replace(sm,
                @"[ \t]*// " + marker + @" START[\s\S]*?// " + marker + @" END[ \t]*\r?\n?",
                "");

            // ⚠️ 位置很关键：**不能**在任务开始时检查"下一步"的前置条件——
            //    那时本步还没执行，下一步当然不满足，会**每一步都必然误报**（实测踩过：
            //    "取圆石"刚宣布开始就警告"制作熔炉 缺 cobblestone 64 个"，而随后取物是成功的）。
            //    正确位置：**本步成功完成之后**，检查下一步现在是否真的具备条件。
            const string doneAnchor =
                "                        console.log(`[StateMachine] 【${todo.task_name}】已完成！`);";
            if (sm.Contains(doneAnchor))
            {
                var block =
                    "                        // " + marker + " START\n" +
                    "                        // 步骤间验真：本步声称成功后，确认**下一步**的前置条件是否真的满足了。\n" +
                    "                        // 不满足说明本步没有真正交付（例如合成被服务器回滚），后续任务必然连带失败。\n" +
                    "                        try {\n" +
                    "                            const __next = this.currentPlan.todo_list[this.currentIndex + 1];\n" +
                    "                            if (__next && __next.condition && Object.keys(__next.condition).length > 0) {\n" +
                    "                                const __miss = ConditionParser.evaluate(this.bot, __next.condition);\n" +
                    "                                if (__miss.length > 0) {\n" +
                    "                                    console.log(`[StateMachine] ⚠️ 连贯性警告：上一步【${todo.task_name}】声称完成，但下一步【${__next.task_name}】的条件仍未满足: ${__miss.join(', ')}`);\n" +
                    "                                    console.log(`[StateMachine] ⚠️ 说明上一步没有真正交付，后续任务很可能连带失败。`);\n" +
                    "                                    if (process.env.SUPPRESS_ACTION_CHAT !== 'true') {\n" +
                    "                                        sendToOwner(this.bot, `⚠️ 上一步【${todo.task_name}】好像没真正完成，接下来要做【${__next.task_name}】还缺: ${__miss.join(', ')}。`);\n" +
                    "                                    }\n" +
                    "                                }\n" +
                    "                            }\n" +
                    "                        } catch (e) {\n" +
                    "                            console.log(`[StateMachine] 步骤间验真出错（不影响执行）: ${e.message}`);\n" +
                    "                        }\n" +
                    "                        // " + marker + " END\n";
                sm = sm.Replace(doneAnchor, doneAnchor + "\n" + block);
            }
            else
            {
                Console.WriteLine($"[Patch] 警告：{smFile} 未找到任务完成日志锚点，步骤间验真未注入");
            }

            sm = Regex.Replace(sm, @"(\r?\n){3,}", "\n\n");
            if (sm != smOriginal)
            {
                File.WriteAllText(smFile, sm, Encoding.UTF8);
                Console.WriteLine($"[Patch] 步骤间验真(StateMachine)已注入: {smFile}");
                VerifyJsSyntax(smFile);
            }
        }
    }

    /// <summary>
    /// 把 _craftWithTable 里的手工开窗段换成单行 bot.craft（消除双窗口竞态）。
    /// **对称且自修复**：
    ///   · 注入态（带 marker 的单行）→ 先还原成上游 4 行
    ///   · 上游态 → 换成单行
    ///   · 若方法体为空（历史上被剥离逻辑误删过）→ 直接把单行补回 `if (!tableBlock) {...}` 之后
    /// </summary>
    private static string ApplyCraftWithTableFix(string content, string upstreamBlock, string patchedLine)
    {
        content = content.Replace(patchedLine, upstreamBlock);   // 注入态 → 上游态
        content = content.Replace(upstreamBlock, patchedLine);   // 上游态 → 注入态

        if (content.Contains(patchedLine)) return content;

        // 自修复：方法体空了，补回单行
        var lines = new List<string>(content.Split('\n'));
        var mIdx = lines.FindIndex(l => l.Contains("async _craftWithTable(recipe, count, tableBlock)"));
        if (mIdx < 0) return content;

        // 找到方法内 `if (!tableBlock) { ... }` 的收尾 `}`
        var rIdx = lines.FindIndex(mIdx, l => l.Contains("await this.bot.craft(recipe, count, null);"));
        if (rIdx < 0) return content;
        var gIdx = lines.FindIndex(rIdx, l => l.Trim() == "}");
        if (gIdx < 0) return content;

        lines.InsertRange(gIdx + 1, new[] { "", patchedLine });
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 「协议解析错误洪泛」修复（26.2 服务器 + ViaProxy 场景）。
    ///
    /// 实测（2026-10-02 14:08）：服务器升级到 26.2 后，ViaProxy 转出来的 `world_particles`
    /// 用 mineflayer 的 1.21.4 解析器读不了，13 秒内产生 2463 次 PartialReadError，
    /// 日志被刷到 2.9 MB，机器人随即猝死（退出码 -1）。
    ///
    /// 关键：**坏包本身不会让机器人崩**。protodef 的 `FullPacketParser._transform`
    /// 对 `partialReadError` 已经 `return cb()` 静默跳过；真正致命的是它随后那行
    /// `console.log(e.stack)`。而 minecraft-protocol 的 `createDeserializer` 本来就支持
    /// `noErrorLogging`，由 `createBot` 的 `hideErrors` 选项透传。
    ///
    /// 兜底（按用户要求，防止真的错位）：`hideErrors` 只关日志，不改变"跳过坏包"的行为；
    /// 帧层是长度前缀切包（`Splitter`），坏包天然隔离、不影响后续包。
    /// 但仍然追加一个限流的 `uncaughtException` 处理器：万一出现真正的解析错位，
    /// 记录后**不退出进程**，避免机器人猝死。
    /// </summary>
    private static void PatchParseErrorFlood(string srcDir)
    {
        // 三段各自独立的标记：解析错误洪泛修复曾因"两段共用同一个 marker"，
        // 后一段的 RemoveAll 把前一段刚插入的行删掉，反复失败。**必须互不为子串**。
        const string hideMark = "__bakabot_hideerr__";   // createBot 的 hideErrors 行
        const string ueMark = "__bakabot_ue__";         // uncaughtException 兜底块

        var file = Path.Combine(srcDir, "index.js");
        if (!File.Exists(file)) return;

        var content = File.ReadAllText(file, Encoding.UTF8).Replace("\r\n", "\n");
        var original = content;

        var lines = new List<string>(content.Split('\n'));

        // ---- 1) 剥离：两个标记**互不重叠**，用 Contains 一句全清 ----
        // ⚠️ 不能用 EndsWith：hideErrors 那行的标记在**行中间**（后面还跟中文说明），
        //    曾因此剥离失败、每轮重复注入一份 hideErrors（机器人起不来）。
        lines.RemoveAll(l => l.Contains(hideMark, StringComparison.Ordinal) ||
                             l.Contains(ueMark, StringComparison.Ordinal));

        // ---- 2) 注入 A：createBot 选项加 hideErrors: true ----
        //     放在所有注入的最前面，保证它在文件里的位置与 createBot 相邻
        var cbIdx = lines.FindIndex(l => Regex.IsMatch(l, @"createBot\s*\(\s*\{"));
        if (cbIdx >= 0)
        {
            var ind = new string(' ', lines[cbIdx].Length - lines[cbIdx].TrimStart().Length + 4);
            lines.Insert(cbIdx + 1,
                ind + "hideErrors: true, // " + hideMark + "：解析错误只跳过不刷屏（26.2 粒子包读不了会刷爆日志）");
        }
        else
        {
            Console.WriteLine($"[Patch] 警告：{file} 未找到 createBot 锚点，hideErrors 未注入");
        }

        // ---- 3) 注入 B：限流的 uncaughtException 兜底（真错位时记录但不猝死）----
        var anchor = lines.FindIndex(l => l.Contains("process.on('unhandledRejection'"));
        if (anchor >= 0)
        {
            var end = lines.FindIndex(anchor, l => l.Trim() == "});");
            if (end >= 0)
            {
                var M = " // " + ueMark;
                lines.InsertRange(end + 1, new[]
                {
                    "",
                    "process.on('uncaughtException', (err) => {" + M,
                    "    __bakabotUncaughtCount = (__bakabotUncaughtCount || 0) + 1;" + M,
                    "    if (__bakabotUncaughtCount <= 5 || __bakabotUncaughtCount % 500 === 0) {" + M,
                    "        console.error('[Bot] 未捕获异常(已忽略，防猝死) #' + __bakabotUncaughtCount + ':', (err && err.message) || err);" + M,
                    "    }" + M,
                    "});" + M
                });
            }
            else Console.WriteLine($"[Patch] 警告：{file} 未找到 unhandledRejection 处理器结尾，兜底未注入");
        }
        else Console.WriteLine($"[Patch] 警告：{file} 未找到 unhandledRejection 锚点，兜底未注入");

        content = string.Join("\n", lines);
        content = Regex.Replace(content, @"(\r?\n){3,}", "\n\n");

        if (content != original)
        {
            File.WriteAllText(file, content, Encoding.UTF8);
            Console.WriteLine($"[Patch] 协议解析错误洪泛修复已注入: {file}");
            VerifyJsSyntax(file);
        }
    }
    /// <summary>剥离上一轮注入的 dump helper 块（显式边界，保留后面的分隔空行）</summary>
    private static string StripDumpHelper(string content, string startMark, string endMark)
    {
        return Regex.Replace(content,
            @"[ \t]*" + Regex.Escape(startMark) + @"[\s\S]*?" + Regex.Escape(endMark) + @"[ \t]*\r?\n",
            "");
    }

    /// <summary>
    /// 改完 JS 后立刻做语法自检（node --check）。
    /// 本补丁曾两次把注入点写错导致语法坏掉，靠这个检查能当场发现而不是等用户实测。
    /// 找不到 node 就跳过（不阻断补丁流程）。
    /// </summary>
    private static void VerifyJsSyntax(string file)
    {
        try
        {
            var node = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Bakabot", "runtime", "node.exe");
            if (!File.Exists(node)) return;

            var psi = new ProcessStartInfo(node)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--check");
            psi.ArgumentList.Add(file);

            using var p = Process.Start(psi);
            if (p == null) return;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);

            if (p.ExitCode != 0)
                Console.WriteLine($"[Patch] ⚠️ 语法自检失败: {file}{Environment.NewLine}{err.Trim()}");
        }
        catch
        {
            // 自检本身不应该影响打补丁
        }
    }

    /// <summary>
    /// 把 dump helper 注入到「构造器闭合之后、首个类方法之前」。
    /// 整块替换后立刻用 node --check 语法自检，避免再次把语法改坏。
    /// </summary>
    private static string InjectDumpHelper(string content, string startMark, string endMark)
    {
        var helper =
            "    " + startMark + "\n" +
            "    // 任务连贯性诊断：打印当前真实背包 + 手持 + 当前窗口状态。\n" +
            "    // 只在 DEBUG_MODE=true 时输出，异常一律吞掉——诊断绝不能影响主流程。\n" +
            "    async _bakabotDumpInv(tag) {\n" +
            "        if (process.env.DEBUG_MODE !== 'true') return;\n" +
            "        try {\n" +
            "            const inv = this.bot.inventory.items();\n" +
            "            const list = inv.map(i => `${i.name}x${i.count}`).join(', ') || '(空)';\n" +
            "            const held = this.bot.heldItem ? `${this.bot.heldItem.name}x${this.bot.heldItem.count}` : '无';\n" +
            "            const win = this.bot.currentWindow ? `${this.bot.currentWindow.type}(id=${this.bot.currentWindow.id})` : '无';\n" +
            "            console.log(`[连贯性诊断][${tag}] 背包=${list} | 手持=${held} | 当前窗口=${win}`);\n" +
            "        } catch (e) {\n" +
            "            console.log(`[连贯性诊断][${tag}] 打印背包失败: ${e.message}`);\n" +
            "        }\n" +
            "    }\n" +
            "    // 按名统计背包数量（产出校验用）。\n" +
            "    // ⚠️ 这里刻意只做**精确名匹配**，不依赖 TakeItems.js 里的附魔匹配 helper：\n" +
            "    //    helper 是逐文件注入的，CraftItem/DropItemAction 并不共享 TakeItems 的方法，\n" +
            "    //    跨文件调用会抛 \"is not a function\"，反而让校验永远失败（踩过）。\n" +
            "    _bakabotCountItem(name) {\n" +
            "        try {\n" +
            "            return this.bot.inventory.items()\n" +
            "                .filter(i => i && i.name === name)\n" +
            "                .reduce((s, i) => s + (i.count || 0), 0);\n" +
            "        } catch (e) { return 0; }\n" +
            "    }\n" +
            "    " + endMark + "\n\n";

        // 构造器闭合 -> 首个类方法之间的位置（用正则，容忍 1 个以上空行）
        var ctorEndRe = new Regex(@"(\r?\n)[ \t]*\}[ \t]*(\r?\n)+([ \t]*async execute\(\) \{)");
        if (!ctorEndRe.IsMatch(content))
            return content;   // 由调用方负责告警

        var escaped = helper.TrimEnd('\n').Replace("$", "$$");
        return ctorEndRe.Replace(content, "$1    }$1$1" + escaped + "$1$1$3", 1);
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
    global.__bakabotQQAIMsg = false;

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
      global.__bakabotQQAIMsg = false;
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
          return Promise.resolve(__origGen.call(this, sysPrompt, history, userPrompt)).then(function(r) {
            // AI 回复已生成：标记下一条 QQ 输出为 AI 消息（启动器据此做“只回 AI”筛选）
            global.__bakabotQQAIMsg = true;
            return r;
          });
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
              var __ai = !!global.__bakabotQQAIMsg; global.__bakabotQQAIMsg = false;
              if (s.indexOf('/tell ') === 0 || s.indexOf('/msg ') === 0) {
                var parts = s.split(' ');
                if (parts.length > 2) { parts.splice(0, 2); var txt = parts.join(' ').trim(); if (txt) __qqOut(__ai ? 'ai' : 'msg', txt); }
                return;
              }
              if (s.charAt(0) !== '/') { __qqOut(__ai ? 'ai' : 'msg', s); return; }
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

        // 通道判断：**按本次指令的来源快照**判定，不能读活动通道 __bakabotQQ。
        // ⚠️ 这里踩过一个用户可见的 bug（2026-10-03）：QQ 会话结束后 __bakabotQQ 会一直残留，
        //    直到「下一条会被处理的游戏指令」把它清掉。旧代码只看 __bakabotQQ，
        //    于是 QQ 说过话之后，**游戏里第一条命令提示词的回执会被错判成 QQ 来源、发到 QQ**，
        //    在游戏里再说一句才恢复正常（正是用户报的「说一句之后又回来了」）。
        // 归属优先级：
        //   ① global.__bakabotQQByGame === true  → 当前这条指令就是游戏内的（QQ 桥接在
        //      「确实会处理该游戏指令」时置位；比残留的活动通道更可信）
        //   ② __bakabotQQTurn（本次指令快照）
        //   ③ __bakabotQQ（活动通道，兜底）
        var ch = global.__bakabotQQByGame
          ? null
          : ((global.__bakabotQQTurn && global.__bakabotQQTurn.active)
            ? global.__bakabotQQTurn
            : ((global.__bakabotQQ && global.__bakabotQQ.active) ? global.__bakabotQQ : null));
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
        var savedTurn = global.__bakabotQQTurn;
        global.__bakabotQQ = null;
        global.__bakabotQQTurn = null;
        try { bot.chat(cmd); } catch (e) { console.log('[QuickCmd] 执行失败: ' + e.message); return false; }
        finally { global.__bakabotQQ = savedCh; global.__bakabotQQTurn = savedTurn; }

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
    // 【本次指令的回复通道快照】。与上面的「当前通道」分开：
    // QQ 回复是异步生成的（要等 LLM），而「当前通道」会被游戏内事件随时翻转，
    // 若回复落地时读的是当时的全局状态，就会按游戏通道走 → 漏进服务器公聊。
    // 详见 PatchQQBridgeV2 的说明与 __bakabotGameWillRun。
    global.__bakabotQQTurn = null;
    global.__bakabotQQBot = null;
    // 归属标记：true = 当前这条指令来自游戏内（由 emit 包装在「确实会处理该游戏指令」时置位）
    global.__bakabotQQByGame = false;

    // ── 通道判定辅助（输出分流与事件判定共用）──
    // 说话人是否算「主人」之一：按逗号名单**精确**比较（大小写不敏感 + 去空格）。
    // 注意不能用子串比较：多主人名单下「0nderfu」会命中「w0nderfu_11」，
    // 单主人「Cold_」会命中另一个人「Cold_Fan」（基础包原文正是双向子串比较，属历史缺陷）。
    function __bakabotSameUsers(name) {
      var n = String(name || '').trim().toLowerCase();
      if (!n) return false;
      var names = String(process.env.MC_OWNER_NAME || '')
        .split(',').map(function (s) { return s.trim().toLowerCase(); }).filter(Boolean);
      return names.indexOf(n) !== -1;
    }
    // 这条游戏内消息【会不会真的被基座处理】？
    // 基座 handleMessage 有多道门：机器人自己发的、非主人、空消息，以及 isThinking 并发锁。
    // 尤其 isThinking：机器人正在回答 QQ 指令时，游戏内消息会被【直接丢弃】，
    // 此时切通道毫无意义，只会把 QQ 的回复赶到游戏里。
    function __bakabotGameWillRun(name, text) {
      // isThinking 是基座 index.js 模块作用域的 let，与本补丁同模块，可直接读
      if (typeof isThinking !== 'undefined' && isThinking) return false;
      var me = global.__bakabotQQBot && global.__bakabotQQBot.username;
      if (me && String(name || '') === String(me)) return false;
      if (!__bakabotSameUsers(name)) return false;
      if (text !== undefined && text !== null && String(text) === '') return false;
      return true;
    }
    // 当前该走哪个通道：优先【本次指令的快照】，其次「当前通道」
    // __bakabotQQByGame：一旦处理过一条游戏内指令就置位，表示「当前这条指令来自游戏」。
    // 它让输出分流与 QuickCmd 的归属判定不再被残留的活动通道带偏（见 PatchQuickCommands 注释）。
    function __bakabotCh() {
      if (global.__bakabotQQByGame) return null;
      if (global.__bakabotQQTurn && global.__bakabotQQTurn.active) return global.__bakabotQQTurn;
      if (global.__bakabotQQ && global.__bakabotQQ.active) return global.__bakabotQQ;
      return null;
    }
    global.__bakabotQQReady = false;
    global.__bakabotQQAIMsg = false;
    // 按 QQ 号分开记会话历史（每人一份，最多 10 条）
    global.__bakabotQQHist = {};

    function __qqOut(text, ch) {
      try {
        ch = ch || global.__bakabotQQ;
        var __ai = !!global.__bakabotQQAIMsg; global.__bakabotQQAIMsg = false;
        console.log('[QQ-OUT] ' + JSON.stringify({
          type: __ai ? 'ai' : 'msg',
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
      global.__bakabotQQTurn = global.__bakabotQQ; // 本次指令的回复通道快照
      // 这条指令来自 QQ：清掉「来自游戏」的归属标记（见 __bakabotCh 的说明）
      global.__bakabotQQByGame = false;
      // stop 类指令顺带清掉该 QQ 的会话历史（基座同时会清全局历史）
      if (String(msg.text).trim() === 'stop') global.__bakabotQQHist[qq] = [];
      global.__bakabotQQAIMsg = false;
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
        // ⚠️ 说话人改用**不会撞名的内部标记**传给基座，而不是直接用绑定玩家名。
        //
        // 原因（2026-10-02 实测）：基座 handleMessage 会用
        //     if (username === bot.username) return;      // 忽略机器人自己发的消息
        // 把「说话人 == 机器人游戏名」的消息丢掉。而绑定玩家名是用户在白名单里填的，
        // 一旦填成机器人自己的游戏名，该 QQ 号的所有指令就被静默丢弃（不思考、不报错）。
        // 传一个固定标记就与绑定名无关了；标记同时被加进「可用主体」名单，
        // 因此能通过下面的主人判定（见 PatchChatTrigger 里 __bcOwnerNames.push(...)）。
        //
        // 注意：这里只在**传给 handleMessage 的参数**上替换；process.env.MC_OWNER_NAME
        // 仍是真实绑定名，所以动作/私聊收件人/提示词都不受影响。
        var __BQ_MARK = '__bakabot_qq_user__';
        // 闸门：本次确实是 QQ 派发中（基座入口到那句自检之间没有 await，见 PatchChatTrigger 说明）
        global.__bakabotQQDispatching = true;
        var __p = handleMessage(__BQ_MARK, text);
        global.__bakabotQQDispatching = false;
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
        // 读「本次指令的快照」而非当时的全局通道：避免 QQ 的异步回复落地时
        // 已被游戏内事件把通道翻转成游戏，从而漏进服务器公聊（本轮修复的根因）
        var ch = __bakabotCh();
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
              return Promise.resolve(__origGen.call(this, sysPrompt, hist, userPrompt)).then(function(r) {
                global.__bakabotQQAIMsg = true;
                return r;
              });
            }
          } catch(e) {}
          return Promise.resolve(__origGen.call(this, sysPrompt, history, userPrompt)).then(function(r) {
            // AI 回复已生成：标记下一条 QQ 输出为 AI 消息（启动器据此做“只回 AI”筛选）
            global.__bakabotQQAIMsg = true;
            return r;
          });
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
            // 同 sendToOwner：优先读本次指令的快照，避免异步回复被翻转的全局通道带偏
            var ch = __bakabotCh();
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
                if (isCommand && __bakabotGameWillRun(args[0], args[1])) {
                  // 真正的游戏指令到来：切回游戏通道，同时清掉 QQ 回复兜底与本次快照。
                  // ⚠️ 只有这条消息【真的会被基座处理】时才切：机器人正在回答 QQ 指令时
                  //（isThinking 锁）游戏内消息会被丢弃，若这里照样切通道，
                  // QQ 的异步回复落地时就按游戏通道走 → 漏进服务器公聊（本轮修的 bug）。
                  global.__bakabotQQ = null;
                  global.__bakabotQQReply = null;
                  global.__bakabotQQTurn = null;
                  // 归属标记：后续判定（输出分流、QuickCmd）据此认定「当前指令来自游戏」，
                  // 不会再被残留的活动通道带偏。下一条 QQ 指令派发时会清掉它。
                  global.__bakabotQQByGame = true;
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
