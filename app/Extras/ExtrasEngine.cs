using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace Assistail.Extras
{
    /// <summary>
    /// B5: runs extras packs. Reminders fire on a timer (optionally counting only active time) or at a time of day,
    /// wait while it's a bad moment (full screen, do-not-disturb, focus, chat open) and can be clicked (done) or
    /// right-clicked (snooze). One routine (e.g. Pomodoro) can run at a time. Everything happens on one slow
    /// dispatcher timer: no threads, no polling faster than every few seconds.
    /// </summary>
    public sealed class ExtrasEngine : IDisposable
    {
        public const string TagPrefix = "extra:";

        /// <summary>After this much idle time you're "away": active-time reminders start over when you're back.</summary>
        private static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(5);
        /// <summary>Input within this window counts as active time.</summary>
        private const double ActiveIdleSeconds = 90;
        /// <summary>An "at" reminder still fires this long after its time (e.g. the PC was asleep at 18:00).</summary>
        private static readonly TimeSpan AtGrace = TimeSpan.FromHours(3);
        private static readonly TimeSpan NudgeEvery = TimeSpan.FromMinutes(3);
        private const int MaxNudges = 3;
        private const double ReminderSeconds = 30;
        /// <summary>An ignored reminder (escalate on) comes back once after this long, one level more noticeable.</summary>
        private static readonly TimeSpan FollowUpAfter = TimeSpan.FromMinutes(2);
        private const string ClickHint = "Click = done · right-click = snooze";

        /// <summary>Testing aid: ASSISTAIL_EXTRAS_SPEED=60 makes a "minute" one second.</summary>
        public static readonly double Speed =
            double.TryParse(Environment.GetEnvironmentVariable("ASSISTAIL_EXTRAS_SPEED"), NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && s > 0 ? s : 1;

        public static string Folder { get; } = Path.Combine(App.DataDir, "extras");

        private readonly Settings _settings;
        private readonly LionWindow _lion;
        private readonly TrayIcon _tray;
        private readonly Func<bool> _chatOpen;
        private readonly DispatcherTimer _timer;
        private readonly Random _rng = new();
        private readonly PawOverlay _paw = new();
        private DateTime _lastTick = DateTime.Now;

        private sealed class ReminderRun
        {
            public double ActiveSeconds;
            public DateTime Since = DateTime.Now;
            public DateTime SnoozedUntil;
            public bool Pending;
            public DateTime? FollowUpAt;    // ignored: come back once, more noticeably
            public int FollowUpLevel;
        }

        /// <summary>The reminder bubble on screen, to tell "clicked" from "ignored" when it goes away.</summary>
        private (string Tag, Pack Pack, Reminder Reminder, int Level, bool Acted, bool FollowUp)? _shown;
        private readonly Dictionary<string, ReminderRun> _runs = new();

        private sealed class RoutineRun
        {
            public Pack Pack = null!;
            public int Step = -1;           // current step (or the one waiting to start)
            public int Round = 1;
            public DateTime Ends;
            public TimeSpan? PausedLeft;
            public bool Waiting;            // waiting for a click to start Step
            public DateTime NextNudge;
            public int Nudges;
        }
        private RoutineRun? _routine;

        public List<Pack> Packs { get; private set; } = new();
        /// <summary>Raised when the packs or a routine's state change (menus and the Extras window refresh).</summary>
        public event Action? Changed;

        public ExtrasEngine(Settings settings, LionWindow lion, TrayIcon tray, Func<bool> chatOpen)
        {
            _settings = settings;
            _lion = lion;
            _tray = tray;
            _chatOpen = chatOpen;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(Speed > 1 ? 1 : 10) };
            _timer.Tick += (_, _) => Tick();
            _lion.BubbleHidden += OnBubbleHidden;
            _paw.Clicked += tag => { MarkActed(tag); HandleTag(tag); };
            _paw.RightClicked += tag => HandleRightClick(tag);
            _paw.Finished += (tag, _) => OnBubbleHidden(tag);
            _paw.Booped += () => _lion.ShowExtra("swat", null, 0, "extra-done");   // as if it was his paw
            _paw.SetBuddy(_lion.Buddy);
            _lion.BuddyChanged += b => _paw.SetBuddy(b);   // B1: each buddy brings his own paw
            if (!_paw.Available) App.Log("Extras: paw art not found; levels 4 and 5 show as level 3.");
            Reload();
        }

        // ================================================================== packs and their settings

        public void Reload()
        {
            var packs = new List<Pack>();
            try
            {
                if (Directory.Exists(Folder))
                    foreach (var dir in Directory.GetDirectories(Folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                        packs.Add(Pack.Load(dir));
            }
            catch (Exception ex) { App.Log("Extras: couldn't list packs: " + ex.Message); }
            Packs = packs;
            foreach (var p in packs)
                App.Log(p.IsUsable ? $"Extras: loaded {p.Id} {p.Version}{(IsEnabled(p) ? "" : " (off)")}" : $"Extras: {p.Id} not loaded: {p.Error}");

            // Forget timers of packs that went away; a routine whose pack was removed or turned off stops.
            var live = new HashSet<string>(packs.Where(p => p.IsUsable && IsEnabled(p)).SelectMany(p => p.Reminders.Select(r => Key(p, r))));
            foreach (var k in _runs.Keys.ToList()) if (!live.Contains(k)) _runs.Remove(k);
            if (_routine != null && !packs.Any(p => p.Id == _routine.Pack.Id && p.IsUsable && IsEnabled(p))) StopRoutine(quiet: true);

            ApplyTopics();
            UpdateTimer();
            Changed?.Invoke();
        }

        public ExtraState StateOf(Pack p)
        {
            if (!_settings.Extras.TryGetValue(p.Id, out var st)) _settings.Extras[p.Id] = st = new ExtraState();
            return st;
        }

        public bool IsEnabled(Pack p) => !_settings.Extras.TryGetValue(p.Id, out var st) || st.Enabled;

        public void SetEnabled(Pack p, bool on)
        {
            StateOf(p).Enabled = on;
            _settings.Save();
            if (!on && _routine?.Pack.Id == p.Id) StopRoutine(quiet: true);
            if (!on) foreach (var r in p.Reminders) _runs.Remove(Key(p, r));
            ApplyTopics();
            UpdateTimer();
            Changed?.Invoke();
        }

        /// <summary>The value of a pack setting: the user's choice if valid, else the pack's default.</summary>
        public string Value(Pack p, string key)
        {
            var s = p.Setting(key);
            if (s == null) return "";
            return _settings.Extras.TryGetValue(p.Id, out var st) && st.Values.TryGetValue(key, out var v) && s.IsValid(v) ? v : s.Default;
        }

        /// <summary>Resolves "{setting}" to its value; anything else is returned as is.</summary>
        private string Resolve(Pack p, string raw)
        {
            var m = Regex.Match(raw, @"^\{([a-zA-Z0-9_-]+)\}$");
            return m.Success ? Value(p, m.Groups[1].Value) : raw;
        }

        private double Number(Pack p, string raw) =>
            raw.Length == 0 ? 0 : double.TryParse(Resolve(p, raw), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;

        private static string Key(Pack p, Reminder r) => $"{p.Id}:r{r.Index}";

        /// <summary>The level a message shows at: the user's choice for the pack, else the action's, else the inherited one.</summary>
        private int Level(Pack p, PackAction? say, int inherited)
        {
            int user = _settings.Extras.TryGetValue(p.Id, out var st) ? st.Attention : 0;
            if (user is >= 1 and <= Reminder.MaxAttention) return user;
            if (say is { Attention: > 0 }) return say.Attention;
            return Math.Clamp(inherited, 1, Reminder.MaxAttention);
        }

        /// <summary>Whether ignored messages come back more noticeably: the user's choice, else the pack's.</summary>
        private bool Escalates(Pack p, bool packDefault) =>
            _settings.Extras.TryGetValue(p.Id, out var st) && st.Escalate is bool b ? b : packDefault;

        /// <summary>The pack's own default for "escalate" (any reminder or step has it), shown in the Extras window.</summary>
        public static bool PackEscalates(Pack p) => p.Reminders.Any(r => r.Escalate) || (p.Routine?.Steps.Any(s => s.Escalate) ?? false);

        private void ApplyTopics()
        {
            _lion.SuppressedTopics.Clear();
            foreach (var p in Packs)
                if (p.IsUsable && IsEnabled(p))
                    foreach (var t in p.Topics) _lion.SuppressedTopics.Add(t);
        }

        /// <summary>The timer only runs while there's something to do (battery).</summary>
        private void UpdateTimer()
        {
            bool needed = _routine != null || Packs.Any(p => p.IsUsable && IsEnabled(p) && p.Reminders.Count > 0);
            if (needed && !_timer.IsEnabled) { _lastTick = DateTime.Now; _timer.Start(); }
            else if (!needed && _timer.IsEnabled) _timer.Stop();
            UpdateStatus();
        }

        // ================================================================== the tick

        private void Tick()
        {
            var now = DateTime.Now;
            double elapsed = Math.Clamp((now - _lastTick).TotalSeconds, 0, 30);   // a sleeping PC doesn't count
            _lastTick = now;
            double idle = Native.IdleSeconds();
            bool active = idle < ActiveIdleSeconds;
            bool away = idle > AwayAfter.TotalSeconds;

            foreach (var p in Packs)
            {
                if (!p.IsUsable || !IsEnabled(p)) continue;
                foreach (var r in p.Reminders)
                {
                    string key = Key(p, r);
                    if (!_runs.TryGetValue(key, out var run)) _runs[key] = run = new ReminderRun();
                    if (r.Every.Length > 0)
                    {
                        if (r.ActiveTimeOnly && away) { run.ActiveSeconds = 0; run.Pending = false; continue; }
                        if (active) run.ActiveSeconds += elapsed;
                        double interval = Number(p, r.Every) * 60 / Speed;
                        bool due = r.ActiveTimeOnly ? run.ActiveSeconds >= interval : (now - run.Since).TotalSeconds >= interval;
                        if (due && now >= run.SnoozedUntil) run.Pending = true;
                    }
                    else if (AtIsDue(p, r, now) && now >= run.SnoozedUntil && active) run.Pending = true;

                    if (run.FollowUpAt is { } at && now >= at && now >= run.SnoozedUntil && !run.Pending && !IsQuiet(r) && CanInterrupt())
                    {
                        run.FollowUpAt = null;
                        Fire(p, r, run, run.FollowUpLevel, followUp: true);
                    }
                    else if (run.Pending && now >= run.SnoozedUntil && !IsQuiet(r) && CanInterrupt()) Fire(p, r, run);
                }
            }
            StepRoutine(now);
            UpdateStatus();
            if (Speed > 1)   // testing: trace the timers
                App.Log($"Extras tick idle={idle:0}s active={active} away={away} " +
                        string.Join(" ", _runs.Select(kv => $"{kv.Key}={kv.Value.ActiveSeconds:0}{(kv.Value.Pending ? "!" : "")}")));
        }

        private bool AtIsDue(Pack p, Reminder r, DateTime now)
        {
            if (!TimeSpan.TryParse(Resolve(p, r.At), CultureInfo.InvariantCulture, out var at)) return false;
            string days = r.Days.Length > 0 ? Resolve(p, r.Days) : "daily";
            if (days == "weekdays" && now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
            var when = now.Date + at;
            if (now < when || now - when > AtGrace) return false;
            string today = now.ToString("yyyy-MM-dd");
            return !(_settings.Extras.TryGetValue(p.Id, out var st) && st.FiredOn.TryGetValue(r.Index.ToString(), out var d) && d == today);
        }

        private bool IsQuiet(Reminder r)
        {
            foreach (var rule in r.QuietWhen)
            {
                switch (rule)
                {
                    case "fullscreen" when TaskbarInfo.IsFullscreenAppActive(): return true;
                    case "dnd" when Native.NotificationsSuppressed(): return true;
                    case "focus" when InFocusStep: return true;
                    case "chat" when _chatOpen(): return true;
                }
            }
            return false;
        }

        /// <summary>Don't talk over another message (an alert, an update, another reminder).</summary>
        private bool CanInterrupt() => _lion.BubbleTag == null && !_paw.Busy;

        private bool InFocusStep => _routine is { Waiting: false, PausedLeft: null } r && r.Pack.Routine!.Steps[r.Step].Focus;

        private void Fire(Pack p, Reminder r, ReminderRun run, int level = 0, bool followUp = false)
        {
            run.Pending = false;
            run.FollowUpAt = null;
            run.ActiveSeconds = 0;
            run.Since = DateTime.Now;
            if (r.At.Length > 0)
            {
                StateOf(p).FiredOn[r.Index.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
                _settings.Save();
            }
            var say = r.Do.LastOrDefault(a => a.Kind == "say");
            if (level == 0) level = Level(p, say, r.Attention);
            App.Log($"Extras: {p.Id} reminder {r.Index} (attention {level}{(followUp ? ", follow-up" : "")})");
            bool actionable = r.OnClick.Count > 0 || r.Snooze > 0;
            string tag = TagPrefix + Key(p, r);
            _shown = null;
            if (Run(p, r.Do, tag, actionable ? ClickHint : null, ReminderSeconds, attention: level))
                _shown = (tag, p, r, level, false, followUp);
        }

        /// <summary>A reminder bubble went away without a click: maybe come back once, more noticeably.</summary>
        private void OnBubbleHidden(string? tag)
        {
            if (_shown is not { } s || tag != s.Tag) return;
            _shown = null;
            if (s.Acted || s.FollowUp || !Escalates(s.Pack, s.Reminder.Escalate)) return;
            if (!_runs.TryGetValue(Key(s.Pack, s.Reminder), out var run)) return;
            run.FollowUpAt = DateTime.Now.AddSeconds(FollowUpAfter.TotalSeconds / Speed);
            run.FollowUpLevel = Math.Min(Reminder.MaxAttention, s.Level + 1);
            App.Log($"Extras: {s.Pack.Id} reminder {s.Reminder.Index} ignored; again at attention {run.FollowUpLevel}");
        }

        private void MarkActed(string tag)
        {
            if (_shown is { } s && s.Tag == tag) _shown = s with { Acted = true };
        }

        // ================================================================== actions

        /// <summary>Runs actions: counters first (so the text shows the new count), then one trick and one line.</summary>
        /// <returns>True if the message is on screen with this tag (bubble or paw), false if not shown or sent to the tray.</returns>
        private bool Run(Pack p, List<PackAction> actions, string tag, string? hint = null, double seconds = 15, double? minutes = null, int attention = 0)
        {
            foreach (var a in actions) if (a.Kind == "count") _settings.AddToCounter(a.Value);
            string? trick = actions.FirstOrDefault(a => a.Kind == "trick")?.Value;
            var say = actions.LastOrDefault(a => a.Kind == "say");
            string? text = say != null ? Fill(p, say.Lines[_rng.Next(say.Lines.Count)], minutes) : null;
            int level = attention > 0 ? attention : Level(p, say, 2);
            double secs = say?.Seconds ?? (level >= 3 ? Math.Max(seconds, 45) : level <= 1 ? Math.Min(seconds, 12) : seconds);
            if (trick == null && text == null) return false;
            if (level >= 4 && text != null && _lion.CanShowMessages)
            {
                // The paw never appears over a full-screen app, a presentation or do-not-disturb: a walk-over instead.
                string? why = _testing ? null
                    : TaskbarInfo.IsFullscreenAppActive() ? "a full-screen app is in front"
                    : Native.NotificationsSuppressed() ? "Windows is busy / do-not-disturb"
                    : !_paw.Available ? "no paw art" : null;
                if (why != null) { App.Log($"Extras: paw not shown ({why}); attention 3 instead"); level = 3; }
                else
                {
                    var work = _lion.MonitorWorkAreaPx;
                    var screen = System.Windows.Forms.Screen.FromRectangle(work).Bounds;
                    bool shown = level >= 5 ? _paw.ShowBoop(text, hint, tag, screen, work) : _paw.ShowSign(text, hint, tag, screen, work);
                    if (shown)
                    {
                        _lion.DismissBubble();
                        if (level == 4) _lion.ShowExtra("wave", null, 0, "extra-done");
                        return true;
                    }
                    level = 3;
                }
            }
            if (!_lion.ShowExtra(trick, text, secs, tag, hint, Math.Min(level, 3)) && text != null)
            {
                // He's away or behind a full-screen app: a tray notification instead (a click acts like clicking him).
                int nl = text.IndexOf('\n');
                _tray.Notify(nl > 0 ? text[..nl] : p.Name, nl > 0 ? text[(nl + 1)..] : text, () => HandleTag(tag));
                return false;
            }
            return text != null && _lion.BubbleTag == tag;
        }

        /// <summary>{name}, {minutes}, {round}, {count:x} and {setting} placeholders.</summary>
        private string Fill(Pack p, string text, double? minutes) =>
            Regex.Replace(text, @"\{([a-zA-Z0-9_:-]+)\}", m =>
            {
                string k = m.Groups[1].Value;
                if (k == "name") return _settings.NameOrDefault;
                if (k == "minutes" && minutes != null) return minutes.Value.ToString("0.#", CultureInfo.InvariantCulture);
                if (k == "round" && _routine != null) return _routine.Round.ToString();
                if (k.StartsWith("count:")) return _settings.Counter(k[6..]).ToString();
                return p.Setting(k) != null ? Value(p, k) : m.Value;
            });

        // ================================================================== clicks on his bubble

        /// <summary>A click on him or his bubble while a pack's message shows. True if handled.</summary>
        public bool HandleClick()
        {
            string? tag = _lion.BubbleTag;
            if (tag == null || !tag.StartsWith(TagPrefix) || tag.EndsWith(":step")) return false;   // a step message: a click on him still opens the chat
            MarkActed(tag);
            _lion.DismissBubble();
            HandleTag(tag);
            return true;
        }

        private void HandleTag(string tag)
        {
            var (pack, what) = ParseTag(tag);
            if (pack == null) return;
            App.Log($"Extras: clicked {tag}");
            if (what == "wait") { if (_routine?.Pack == pack && _routine.Waiting) StartStep(_routine.Step); return; }
            if (what.StartsWith("r") && int.TryParse(what[1..], out int i) && i < pack.Reminders.Count && pack.Reminders[i].OnClick.Count > 0)
                Run(pack, pack.Reminders[i].OnClick, "extra-done", seconds: 5);
        }

        /// <summary>Right-click on a reminder bubble: snooze it. True if handled.</summary>
        public bool HandleRightClick(string? tag)
        {
            if (tag == null || !tag.StartsWith(TagPrefix)) return false;
            MarkActed(tag);
            var (pack, what) = ParseTag(tag);
            if (pack == null || !what.StartsWith("r") || !int.TryParse(what[1..], out int i) || i >= pack.Reminders.Count) return false;
            var r = pack.Reminders[i];
            double minutes = r.Snooze > 0 ? r.Snooze : 10;
            if (_runs.TryGetValue(Key(pack, r), out var run))
            {
                run.SnoozedUntil = DateTime.Now.AddSeconds(minutes * 60 / Speed);
                run.Pending = true;   // fires as soon as the snooze is over
                if (r.At.Length > 0) { StateOf(pack).FiredOn.Remove(r.Index.ToString()); _settings.Save(); }
            }
            App.Log($"Extras: {pack.Id} reminder {i} snoozed {minutes} min");
            _lion.ShowExtra(null, $"OK, I'll remind you again in {minutes:0} minutes ⏰", 4, "extra-done");
            return true;
        }

        private (Pack?, string) ParseTag(string tag)
        {
            var parts = tag[TagPrefix.Length..].Split(':');
            if (parts.Length != 2) return (null, "");
            return (Packs.FirstOrDefault(p => p.Id == parts[0] && p.IsUsable), parts[1]);
        }

        // ================================================================== routines (Pomodoro)

        public Pack? RunningRoutine => _routine?.Pack;
        public bool RoutinePaused => _routine?.PausedLeft != null;

        public void StartRoutine(Pack p)
        {
            if (p.Routine == null || !p.IsUsable) return;
            if (_routine != null) StopRoutine(quiet: true);
            _routine = new RoutineRun { Pack = p };
            App.Log($"Extras: routine {p.Id} started");
            Advance(userStarted: true);
            UpdateTimer();
        }

        public void StopRoutine(bool quiet = false)
        {
            if (_routine == null) return;
            var p = _routine.Pack;
            _routine = null;
            _lion.FocusMode = false;
            _lion.RestPose = null;
            App.Log($"Extras: routine {p.Id} stopped");
            if (!quiet) _lion.ShowExtra("wave", $"{p.Routine!.Label} stopped. Nice work! 👋", 5, "extra-done");
            UpdateTimer();
            Changed?.Invoke();
        }

        public void PauseRoutine()
        {
            if (_routine is not { Waiting: false, PausedLeft: null } r) return;
            r.PausedLeft = r.Ends - DateTime.Now;
            _lion.FocusMode = false;
            _lion.RestPose = null;
            UpdateStatus();
            Changed?.Invoke();
        }

        public void ResumeRoutine()
        {
            if (_routine?.PausedLeft is not { } left) return;
            _routine.Ends = DateTime.Now + left;
            _routine.PausedLeft = null;
            ApplyStepMode(_routine.Pack.Routine!.Steps[_routine.Step]);
            UpdateStatus();
            Changed?.Invoke();
        }

        /// <summary>Skip to the next step (a step skipped early doesn't run its end actions, so it isn't counted).</summary>
        public void SkipStep()
        {
            if (_routine == null) return;
            if (_routine.Waiting) { StartStep(_routine.Step); return; }
            _routine.PausedLeft = null;
            Advance(userStarted: true);
        }

        private int NextStep(out bool newRound)
        {
            var r = _routine!;
            var steps = r.Pack.Routine!.Steps;
            newRound = false;
            int round = r.Round;
            for (int n = 1; n <= steps.Count * 2; n++)
            {
                int i = r.Step + n;
                if (i >= steps.Count * 2) break;
                int idx = i % steps.Count;
                int stepRound = round + (i >= steps.Count ? 1 : 0);
                var s = steps[idx];
                double every = Number(r.Pack, s.Every), except = Number(r.Pack, s.ExceptEvery);
                if (every > 0 && stepRound % (int)every != 0) continue;
                if (except > 0 && stepRound % (int)except == 0) continue;
                newRound = i >= steps.Count;
                return idx;
            }
            return 0;
        }

        private void Advance(bool userStarted)
        {
            var r = _routine!;
            int next = NextStep(out bool newRound);
            if (newRound) r.Round++;
            var step = r.Pack.Routine!.Steps[next];
            r.Step = next;
            if (step.Confirm && !userStarted)
            {
                r.Waiting = true;
                r.Nudges = 0;
                r.NextNudge = DateTime.Now + TimeSpan.FromSeconds(NudgeEvery.TotalSeconds / Speed);
                _lion.FocusMode = false;
                _lion.RestPose = null;
                Changed?.Invoke();
                return;
            }
            StartStep(next);
        }

        private void StartStep(int index)
        {
            var r = _routine!;
            var step = r.Pack.Routine!.Steps[index];
            double minutes = Number(r.Pack, step.Minutes);
            r.Step = index;
            r.Waiting = false;
            r.PausedLeft = null;
            r.Ends = DateTime.Now.AddSeconds(minutes * 60 / Speed);
            App.Log($"Extras: {r.Pack.Id} step {step.Id} ({minutes} min, round {r.Round})");
            ApplyStepMode(step);
            Run(r.Pack, step.Start, TagPrefix + r.Pack.Id + ":step", minutes: minutes);
            UpdateStatus();
            Changed?.Invoke();
        }

        private void ApplyStepMode(RoutineStep step)
        {
            _lion.FocusMode = step.Focus;
            _lion.RestPose = step.Pose.Length > 0 ? step.Pose : null;
        }

        private void StepRoutine(DateTime now)
        {
            var r = _routine;
            if (r == null || r.PausedLeft != null) return;
            if (r.Waiting)
            {
                if (now < r.NextNudge || r.Nudges >= MaxNudges || !CanInterrupt() || TaskbarInfo.IsFullscreenAppActive()) return;
                r.Nudges++;
                r.NextNudge = now + TimeSpan.FromSeconds(NudgeEvery.TotalSeconds / Speed);
                var step = r.Pack.Routine!.Steps[r.Step];
                var nudgeSay = step.Nudge.LastOrDefault(a => a.Kind == "say");
                int level = Level(r.Pack, nudgeSay, 2);
                if (Escalates(r.Pack, step.Escalate)) level = Math.Min(Reminder.MaxAttention, level + r.Nudges);   // each nudge a bit louder
                if (step.Nudge.Count > 0) Run(r.Pack, step.Nudge, TagPrefix + r.Pack.Id + ":wait", seconds: 60, minutes: Number(r.Pack, step.Minutes), attention: level);
                return;
            }
            if (now < r.Ends) return;

            // The step is over: its end actions, then the next step (which may wait for a click).
            var done = r.Pack.Routine!.Steps[r.Step];
            int next = NextStep(out _);
            bool nextWaits = r.Pack.Routine.Steps[next].Confirm;
            Run(r.Pack, done.End, TagPrefix + r.Pack.Id + (nextWaits ? ":wait" : ":step"), seconds: nextWaits ? 60 : 15);
            Advance(userStarted: false);
        }

        // ================================================================== status (tray tooltip, menu)

        /// <summary>"🍅 Focus · 12 min left", "Pomodoro paused", … or null when no routine runs.</summary>
        public string? RoutineStatus
        {
            get
            {
                var r = _routine;
                if (r == null) return null;
                var step = r.Pack.Routine!.Steps[r.Step];
                string icon = r.Pack.Icon.Length > 0 ? r.Pack.Icon + " " : "";
                if (r.Waiting) return $"{icon}Ready for {step.Name.ToLowerInvariant()}";
                var left = r.PausedLeft ?? (r.Ends - DateTime.Now);
                double realMin = Math.Max(0, left.TotalMinutes * Speed);
                string time = realMin >= 1 ? $"{Math.Ceiling(realMin):0} min" : "<1 min";
                return $"{icon}{step.Name} · {time} left" + (r.PausedLeft != null ? " (paused)" : "");
            }
        }

        public string? WaitingStepName => _routine is { Waiting: true } r ? r.Pack.Routine!.Steps[r.Step].Name : null;

        private void UpdateStatus() => _tray.SetStatus(RoutineStatus);

        /// <summary>Testing aid (ASSISTAIL_TRICK=extra:stretch): fires a pack's first reminder now, or starts its routine.</summary>
        private bool _testing;

        public void TestFire(string id)
        {
            _testing = true;
            try { TestFireCore(id); } finally { _testing = false; }
        }

        private void TestFireCore(string id)
        {
            int level = 0;
            int at = id.IndexOf('@');
            if (at > 0) { int.TryParse(id[(at + 1)..], out level); id = id[..at]; }
            var p = Packs.FirstOrDefault(x => x.Id == id && x.IsUsable);
            if (p == null) { App.Log($"Extras test: no usable pack {id}"); return; }
            if (p.Routine != null) { StartRoutine(p); return; }
            var r = p.Reminders[0];
            if (!_runs.TryGetValue(Key(p, r), out var run)) _runs[Key(p, r)] = run = new ReminderRun();
            Fire(p, r, run, Math.Clamp(level, 0, Reminder.MaxAttention));
        }

        public void Dispose()
        {
            _timer.Stop();
            _lion.BubbleHidden -= OnBubbleHidden;
            _paw.Stop(raise: false);
            _paw.Dispose();
            _lion.FocusMode = false;
            _lion.RestPose = null;
        }
    }
}
