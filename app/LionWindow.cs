using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Assistail
{
    /// <summary>
    /// The lion that sits on the taskbar: a native layered window (see <see cref="LayeredSurface"/>) whose frames
    /// are composed in memory (ground shadow + sprite). Handles hover/click/drag, chat reactions, leaving and
    /// returning, tricks (manual or random when idle), the butterfly game, pointer chasing and the speech bubble.
    /// Everything is in physical pixels.
    /// </summary>
    public sealed class LionWindow : IDisposable
    {
        private enum Mode { Sitting, Leaving, Away, Arriving, Strolling, Chasing, Playing, ClothPlay }

        public static readonly (string Id, string Label)[] Tricks =
        {
            ("shake_head", "Shake head"), ("jump", "Jump"), ("stretch_yawn", "Stretch && yawn"),
            ("tail_chase", "Chase his tail"), ("wave", "Wave"), ("look_roar", "Look around && roar"),
            ("walk_around", "Walk around"), ("chase", "Chase my pointer"), ("butterfly", "Play with a butterfly"),
        };

        private const double WalkSeconds = 4.2;          // leaving/arriving walk duration (fades out/in)
        private const double WalkDipPerSecM = 42;        // natural pace at medium size
        private const double ChaseSeconds = 10;
        private static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(10);

        private readonly Settings _settings;
        private SpritePlayer _player;
        private readonly SpritePlayer _butterflySprites = new(Buddy.Lion);   // the butterfly's frames ship with the app
        private readonly DispatcherTimer _clock;
        private readonly LayeredSurface _surface;
        private readonly LayeredSurface _butterfly;
        private readonly IntPtr _handCursor = Native.LoadCursor(IntPtr.Zero, 32649);
        private readonly Random _rng = new();
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private SpeechBubble? _bubble;

        private Ground _ground;
        private double _sizeDip;
        private int _px;
        private byte _alpha = 255;
        private Mode _mode;

        // Animation state
        private string _state = "idle";
        private int _frame;
        private int _frameStep = 1;
        private bool _reverse;
        private Action? _onDone;
        private bool _flip;

        // Position
        private double _centerPx;
        private double _dragLiftPx;
        private int _housekeepingTick;
        private bool _hiddenForFullscreen;

        // Walking (leave/arrive/stroll/chase)
        private DateTime _walkStart;
        private double _walkFromPx, _walkTargetPx;
        private bool _walking;
        private DateTime _modeStart;

        // Quirks
        private DateTime _nextQuirk;
        private int _butterflyFrame;
        private double _bfX, _bfY;
        private bool _bfFlip;
        private int _playStep;

        // Mouse
        private bool _mouseDown, _dragging, _tracking;
        private double _grabOffsetPx, _downCursorX;

        public DateTime LastActivity { get; private set; } = DateTime.Now;
        public bool IsAway => _mode is Mode.Away or Mode.Leaving;
        /// <summary>Set by the app while the chat window is open: no random quirks then.</summary>
        public bool ChatOpen
        {
            get => _chatOpen;
            set { _chatOpen = value; if (!value) ScheduleNextQuirk(); }   // no quirk right after the chat closes
        }
        private bool _chatOpen;

        /// <summary>B5: a focus timer is running: no random quirks or self-talk.</summary>
        public bool FocusMode { get; set; }
        /// <summary>B5: what he does instead of sitting idle (e.g. "sleep" during a break), or null.</summary>
        public string? RestPose { get; set; }
        /// <summary>B5: self-talk topics an enabled pack already covers ("water", "stretch", "break"): those lines are skipped.</summary>
        public System.Collections.Generic.HashSet<string> SuppressedTopics { get; } = new();

        public event Action? Clicked;
        public event Action? MenuRequested;
        /// <summary>The speech bubble was clicked (<see cref="BubbleTag"/> still tells which message it was).</summary>
        public event Action? BubbleClicked;
        /// <summary>The speech bubble was right-clicked (B5: snooze a reminder).</summary>
        public event Action? BubbleRightClicked;
        /// <summary>The bubble went away (timed out, clicked or replaced); passes its tag.</summary>
        public event Action<string?>? BubbleHidden;
        /// <summary>A2: files/folders (or dragged text) dropped on him.</summary>
        public event Action<string[], string?>? FilesDropped;

        private readonly DropTarget? _drop;
        private string? _bubbleTag;

        /// <summary>Tag of the message in the visible speech bubble ("alert", "update", …), or null.</summary>
        public string? BubbleTag => _bubble is { IsVisible: true } ? _bubbleTag : null;

        public LionWindow(Settings settings)
        {
            _settings = settings;
            // ASSISTAIL_BUDDY=<id>: testing aid, starts with that buddy without changing the settings.
            _player = new SpritePlayer(Buddy.Find(Environment.GetEnvironmentVariable("ASSISTAIL_BUDDY") ?? settings.Buddy));
            _mode = settings.LionVisible ? Mode.Sitting : Mode.Away;
            _surface = new LayeredSurface("AssistailLion", WndProc);
            _butterfly = new LayeredSurface("AssistailButterfly");
            _clock = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(1000.0 / 15) };
            _clock.Tick += (_, _) => Tick();
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            // Battery: stop animating while the PC is locked or the screen is off.
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _displayNotify = Native.RegisterPowerSettingNotification(_surface.Hwnd, ref Native.GUID_CONSOLE_DISPLAY_STATE, 0);
            ScheduleNextQuirk();

            _drop = DropTarget.Register(_surface.Hwnd);
            if (_drop != null)
            {
                _drop.Hover += OnDragHover;
                _drop.Dropped += (files, text) => { Touch(); FilesDropped?.Invoke(files, text); };
            }
        }

        private void OnDragHover(bool over)
        {
            if (over)
            {
                Touch();
                if (CanReact && _state is "idle" or "sleep" or "hover") Play("hover", () => Play("idle"));
                if (BubbleTag is null or "drop") Say("Drop it on me and ask me about it 📎", 30, "drop", talk: false);
            }
            else if (BubbleTag == "drop") _bubble?.Dismiss();
        }

        // ================================================================== power: pause when nobody can see him

        private readonly IntPtr _displayNotify;
        private bool _pausedLocked, _pausedDisplayOff;

        private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) => _dispatcher.BeginInvoke(() =>
        {
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
                _pausedLocked = true;
            else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
                _pausedLocked = false;
            UpdatePause();
        });

        private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e) => _dispatcher.BeginInvoke(() =>
        {
            if (e.Mode == PowerModes.Resume) { _pausedDisplayOff = false; UpdatePause(); Layout(force: true); }
        });

        private void UpdatePause()
        {
            bool paused = _pausedLocked || _pausedDisplayOff;
            if (paused)
            {
                if (_clock.IsEnabled) App.Log("Lion paused (locked or screen off).");
                _clock.Stop();
            }
            else if (!_clock.IsEnabled && _mode != Mode.Away)
            {
                App.Log("Lion resumed.");
                Touch();                      // greet with full-speed animation
                _clock.Start();
                Layout(force: true);
            }
        }

        // ================================================================== public API

        public void Start()
        {
            if (_mode == Mode.Away) return;
            Layout(force: true);
            Play("idle");
            var (hx, hy, _) = HeadPx();
            App.Log($"Buddy '{Buddy.Id}' at x={_centerPx:0}, head at ({hx:0},{hy:0})");
            _surface.Show();
            Render(force: true);
            _clock.Start();
        }

        public void UpdateName() { /* the name is shown in the tray tooltip, chat, menus and speech */ }

        // ================================================================== B1: which buddy

        public Buddy Buddy => _player.Buddy;
        public event Action<Buddy>? BuddyChanged;

        /// <summary>The tricks for the menu and the trick hotkey: the shared ones (with this buddy's labels) + his own.</summary>
        public System.Collections.Generic.List<(string Id, string Label)> TrickList
        {
            get
            {
                var list = new System.Collections.Generic.List<(string, string)>();
                foreach (var (id, label) in Tricks) list.Add((id, Buddy.Label(id, label)));
                if (Buddy.Has("cloth") && _player.Has("untie_success")) list.Insert(5, ("untie", "Try to untie the cloth"));
                return list;
            }
        }

        /// <summary>Swaps in another buddy with a little hop (keeps his spot, name and settings).</summary>
        public void SetBuddy(Buddy buddy)
        {
            if (buddy.Id == Buddy.Id) return;
            App.Log($"Buddy: {Buddy.Id} -> {buddy.Id}");
            EndClothGame();
            if (_mode is Mode.Strolling or Mode.Chasing or Mode.Playing)
            {
                _butterfly.Hide();
                _walking = false; _walkSpeedMul = 1; _homing = false; _awayFromSpot = false; _visitTag = null;
                _mode = Mode.Sitting;
                _flip = false;
            }
            _player = new SpritePlayer(buddy);
            // The old buddy may be mid-way through an animation the new one doesn't have (the cat's untie, a lick…):
            // start from idle before anything draws, and drop callbacks that belong to the old animation.
            _state = "idle"; _frame = 0; _frameStep = 1; _reverse = false; _onDone = null;
            _clothSprites = null;
            _settings.Buddy = buddy.Id;
            _settings.Save();
            _lastDrawn = default;
            Layout(force: true);
            if (_mode == Mode.Away) { BuddyChanged?.Invoke(buddy); return; }
            Touch();
            Play("landing", () => Play("happy", () => Play("idle")));   // plops in, then a happy hop
            Render(force: true);
            BuddyChanged?.Invoke(buddy);
        }
        public void ApplySize() => Layout(force: true);

        public System.Drawing.Rectangle BodyBoundsPx
        {
            get
            {
                var idle = _player["idle"];
                double half = idle.ShadowW / 2 * _px;
                int top = (int)(_ground.Y - (idle.BaselineY - 0.32) * _px);
                return new System.Drawing.Rectangle((int)(_centerPx - half), top, (int)(half * 2), _ground.Y - top);
            }
        }

        public System.Drawing.Rectangle MonitorWorkAreaPx =>
            new(_ground.WorkArea.Left, _ground.WorkArea.Top, _ground.WorkArea.Width, _ground.WorkArea.Height);

        public void ChatThinking() { if (CanReact) Play("thinking"); Touch(); }
        public void ChatTalking() { if (CanReact) Play("talking"); Touch(); }
        public void ChatDone(bool ok) { if (CanReact) { if (ok) Play("happy", () => Play("idle")); else Play("idle"); } Touch(); }
        public void ChatIdle() { if (CanReact && _state is "thinking" or "talking") Play("idle"); }
        public void ChatOpened() { if (CanReact && _state is "idle" or "sleep" or "hover") Play("wave", () => Play("idle")); Touch(); }

        /// <summary>Runs a trick by id (see <see cref="Tricks"/>), from the menu or the random scheduler.</summary>
        public void DoTrick(string id)
        {
            if (_dragging || _mode is Mode.Away or Mode.Leaving or Mode.Arriving) return;
            if (_clothOff) EndClothGame();
            if (_mode != Mode.Sitting)
            {
                // Another moving trick is running: stop it, sit down, then do the new one.
                _pendingTrick = id;
                InterruptTrick();
                return;
            }
            RunTrick(id);
        }

        private string? _pendingTrick;

        private void RunTrick(string id)
        {
            App.Log("Trick: " + id);
            switch (id)
            {
                case "__leave": Leave(); break;
                case "walk_around": StartStroll(); break;
                case "chase": StartChase(); break;
                case "butterfly": StartButterfly(); break;
                case "untie": TryUntie(); break;
                case "untie_success": TryUntie(true); break;     // (forced outcomes: testing aid / packs)
                case "untie_giveup": TryUntie(false); break;
                default: if (_player.Has(id)) Play(id, () => Play("idle")); break;
            }
        }

        /// <summary>
        /// Shows text in a speech bubble above his head while he "talks". <paramref name="tag"/> marks what the message
        /// is about, so a click on him or the bubble can act on it (see <see cref="BubbleTag"/>).
        /// </summary>
        public void Say(string text, double? seconds = null, string? tag = null, bool talk = true)
        {
            if (string.IsNullOrWhiteSpace(text) || _mode is Mode.Away or Mode.Leaving) return;
            _bubble ??= CreateBubble();
            if (_bubble.IsVisible && _bubbleTag != tag)
            {
                string? replaced = _bubbleTag;
                _bubbleTag = tag;
                BubbleHidden?.Invoke(replaced);
            }
            _bubbleTag = tag;
            _bubble.Say(text.Trim(), seconds);
            PlaceBubble();
            if (talk && CanReact) Play("talking");
        }

        /// <summary>
        /// A1/C1: an attention message (bubble plus a little animation). False if he can't show it right now
        /// (away, or hidden behind a full-screen app), so the caller can use a tray notification instead.
        /// </summary>
        public bool Alert(string animation, string text, double seconds, string tag)
        {
            if (_mode is Mode.Away or Mode.Leaving || _hiddenForFullscreen || !_surface.Visible) return false;
            Touch();
            Say(text, seconds, tag, talk: false);
            if (CanReact) Play(animation, () => Play("idle"));
            return true;
        }

        /// <summary>
        /// B5: a message from an extras pack: an optional trick plus an optional bubble. False if he can't show the
        /// bubble right now (away or hidden behind a full-screen app), so the caller can use a tray notification.
        /// </summary>
        /// <remarks>
        /// <paramref name="attention"/>: 1 subtle (plain bubble), 2 normal (the bubble bounces), 3 noticeable (he walks
        /// toward the middle of the taskbar with a bigger bubble, then goes back to his spot when it's gone).
        /// </remarks>
        public bool ShowExtra(string? trick, string? text, double seconds, string tag, string? hint = null, int attention = 2)
        {
            if (text != null && (_mode is Mode.Away or Mode.Leaving || _hiddenForFullscreen || !_surface.Visible)) return false;
            Touch();
            bool visit = attention >= 3 && text != null && StartVisit(tag, trick);
            if (text != null) { Say(text, seconds, tag, talk: trick == null && !visit); _bubble?.SetHint(hint); _bubble?.SetAttention(attention); }
            if (trick != null && !visit)
            {
                if (trick is "walk_around" or "chase" or "butterfly") DoTrick(trick);
                else if (CanReact) Play(trick, () => Play("idle"));
            }
            return true;
        }

        // ------------------------------------------------------------------ B5: walk over for a noticeable reminder

        private string? _visitTag;          // the reminder he walked over for (null once its bubble is gone)
        private string? _visitTrick;        // done when he arrives
        private bool _awayFromSpot;         // not on his saved spot because of a visit: walk back when the bubble is gone
        private bool _homing;               // walking back
        private double _walkSpeedMul = 1;

        /// <summary>Walks toward the middle of the taskbar (quickly). False if he can't or is already near it.</summary>
        private bool StartVisit(string tag, string? trick)
        {
            if (_mode != Mode.Sitting || _dragging) return false;
            double half = EdgeHalfPx();
            double mid = (_ground.Left + _ground.Right) / 2.0;
            // Stop a little short of the centre, on his side of it, so he doesn't stand right in the middle.
            double target = Math.Clamp(mid - Math.Sign(mid - _centerPx) * 120 * _ground.Scale, _ground.Left + half, _ground.Right - half);
            if (Math.Abs(target - _centerPx) < 150 * _ground.Scale) return false;
            _visitTag = tag;
            _visitTrick = trick;
            _awayFromSpot = true;
            _walkSpeedMul = 3;
            _mode = Mode.Strolling;
            StandFacing(target > _centerPx ? 1 : -1, () => BeginWalk(target));
            return true;
        }

        private void WalkHome()
        {
            double home = TargetCenterPx();
            if (Math.Abs(home - _centerPx) < 4) { _awayFromSpot = false; return; }
            _homing = true;
            _walkSpeedMul = 3;
            _mode = Mode.Strolling;
            StandFacing(home > _centerPx ? 1 : -1, () => BeginWalk(home));
        }

        /// <summary>B5: whether he can show a message right now (not away, not hidden behind a full-screen app).</summary>
        public bool CanShowMessages => _mode is not (Mode.Away or Mode.Leaving) && !_hiddenForFullscreen && _surface.Visible;

        public void DismissBubble() => _bubble?.Dismiss();

        public void Leave()
        {
            EndClothGame();
            if (_mode is Mode.Strolling or Mode.Chasing or Mode.Playing) { _pendingTrick = "__leave"; InterruptTrick(); return; }
            if (_mode != Mode.Sitting || _dragging) return;
            _bubble?.Dismiss();
            _awayFromSpot = _homing = false;
            _visitTag = null;
            _mode = Mode.Leaving;
            int dir = _centerPx > (_ground.Left + _ground.Right) / 2.0 ? +1 : -1;
            StandFacing(dir, () => BeginWalk(_centerPx + dir * WalkSpeedPx * WalkSeconds));
            _settings.LionVisible = false;
            _settings.Save();
        }

        public void Return()
        {
            if (_mode != Mode.Away && _mode != Mode.Leaving) return;
            _mode = Mode.Arriving;
            Layout(force: true);
            double target = TargetCenterPx();
            int dir = target > (_ground.Left + _ground.Right) / 2.0 ? -1 : +1;   // come in from the nearer edge
            _flip = dir < 0;
            _centerPx = target - dir * WalkSpeedPx * WalkSeconds;
            _alpha = 0;
            Play("walk");
            BeginWalk(target);
            _surface.Show();
            _clock.Start();
            _settings.LionVisible = true;
            ScheduleNextQuirk();                  // a full quirk interval after arriving, not straight away
            Touch();
            _settings.Save();
        }

        public void Close() => Dispose();

        public void Dispose()
        {
            _clock.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            if (_displayNotify != IntPtr.Zero) Native.UnregisterPowerSettingNotification(_displayNotify);
            if (_drop != null) Native.RevokeDragDrop(_surface.Hwnd);
            _bubble?.Close();
            _butterfly.Dispose();
            _clothWin?.Dispose();
            _surface.Dispose();
        }

        // ================================================================== animation core

        private bool CanReact => _mode == Mode.Sitting && !_dragging;
        /// <summary>Any interaction: resets the idle timers and keeps random quirks at least 30 s away.</summary>
        private void Touch()
        {
            LastActivity = DateTime.Now;
            var soonest = DateTime.Now.AddSeconds(30);
            if (_nextQuirk < soonest) _nextQuirk = soonest;
        }
        private double WalkSpeedPx => WalkDipPerSecM * (_sizeDip / 190.0) * _ground.Scale;

        private void Play(string state, Action? onDone = null, bool reverse = false)
        {
            if (_clothOff)
            {
                if (_player.Has(state + "_bare")) state += "_bare";              // idle → idle_bare, turn → turn_bare, …
                else if (!state.EndsWith("_bare") && state != "retie") EndClothGame("Play " + state);   // anything else: the cloth is back on
            }
            if (!_player.Has(state)) state = "idle";
            _state = state;
            _onDone = onDone;
            _reverse = reverse;
            _frameStep = 1;
            _frame = reverse ? _player[state].Frames - 1 : 0;
        }

        /// <summary>Turns to face a direction (+1 right, -1 left) and stands up on four legs.</summary>
        private void StandFacing(int dir, Action then)
        {
            _flip = dir < 0;
            Play("turn", () => Play("stand_up", then));
        }

        /// <summary>Sits back down from standing and turns to face the user.</summary>
        private void SitDown(Action? then = null)
        {
            _walking = false;
            Play("stand_up", () => Play("turn", () =>
            {
                _flip = false;
                _mode = Mode.Sitting;
                Play("idle");
                then?.Invoke();
            }, reverse: true), reverse: true);
        }

        /// <summary>Stops a moving trick (chase, stroll, butterfly) so a newly chosen trick can start.</summary>
        private void InterruptTrick()
        {
            App.Log($"Interrupting {_mode} for {_pendingTrick}");
            switch (_mode)
            {
                case Mode.Playing:
                    _butterfly.Hide();
                    _mode = Mode.Sitting;
                    Play("idle");                       // pending trick starts on the next tick
                    break;
                case Mode.Chasing when _walking:
                    EndChase();
                    break;
                case Mode.Strolling when _walking:
                    _walkSpeedMul = 1;
                    _homing = false;
                    if (!_awayFromSpot) SaveSpot();
                    SitDown();
                    break;
                // Still turning / standing up: BeginWalk sees the pending trick and sits back down instead.
                // Already sitting down: the pending trick runs as soon as he's seated.
            }
        }

        private void BeginWalk(double targetPx)
        {
            if (_pendingTrick != null && _mode is Mode.Strolling or Mode.Chasing) { SitDown(); return; }
            Play("walk");
            if (_walkSpeedMul > 1.5) _frameStep = 2;   // quicker pace, quicker legs
            _walking = true;
            _walkStart = DateTime.Now;
            _walkFromPx = _centerPx;
            _walkTargetPx = targetPx;
        }

        private void Tick()
        {
            var anim = _player[_state];
            for (int step = 0; step < _frameStep; step++)
            {
                if (_reverse)
                {
                    if (--_frame <= 0) { _frame = 0; _reverse = false; Finish(); break; }
                }
                else if (++_frame >= anim.Frames)
                {
                    if (anim.Loop) _frame = 0;
                    else { _frame = anim.Frames - 1; Finish(); break; }
                }
            }

            switch (_mode)
            {
                case Mode.Leaving or Mode.Arriving or Mode.Strolling when _walking: StepWalk(); break;
                case Mode.Chasing: StepChase(); break;
                case Mode.Playing: StepButterfly(); break;
                case Mode.Sitting: StepIdleLife(); break;
            }
            if (_clothOff) StepCloth();
            else if (_mode == Mode.Sitting) StepLick();

            if (++_housekeepingTick >= (Calm ? 16 : 30)) { _housekeepingTick = 0; Housekeeping(); }   // ~every 2 s
            if (_mode != Mode.Away) Render();
            if (_bubble is { IsVisible: true }) PlaceBubble();
            AdjustFrameRate();
        }

        // ================================================================== calm idle (battery)

        private static readonly TimeSpan CalmAfter = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(1000.0 / 15);
        private static readonly TimeSpan CalmInterval = TimeSpan.FromMilliseconds(1000.0 / 8);

        /// <summary>Just sitting (or napping) with no recent interaction: animate gently at ~8 fps.</summary>
        private bool Calm => _mode == Mode.Sitting && !_dragging && _bubble is not { IsVisible: true } &&
                             _state is "idle" or "sleep" && DateTime.Now - LastActivity > CalmAfter;

        private static readonly bool NoAnim = Environment.GetEnvironmentVariable("ASSISTAIL_NOANIM") == "1";   // measurement aid

        private static readonly bool Profile = Environment.GetEnvironmentVariable("ASSISTAIL_PROFILE") == "1";
        private int _profTicks, _profRenders; private double _profMs; private DateTime _profSince = DateTime.Now;
        private readonly System.Diagnostics.Stopwatch _profWatch = new();

        private void ProfileTick()
        {
            _profTicks++;
            if ((DateTime.Now - _profSince).TotalSeconds < 10) return;
            App.Log($"PROFILE ticks={_profTicks} renders={_profRenders} renderMs={_profMs:N1} calm={Calm} interval={_clock.Interval.TotalMilliseconds:N1} state={_state}");
            _profTicks = _profRenders = 0; _profMs = 0; _profSince = DateTime.Now;
        }

        private void AdjustFrameRate()
        {
            if (Profile) ProfileTick();
            if (NoAnim) { _clock.Stop(); return; }
            var want = Calm ? CalmInterval : FastInterval;
            if (_clock.Interval != want) _clock.Interval = want;
        }

        private void Finish()
        {
            var cb = _onDone;
            _onDone = null;
            cb?.Invoke();
        }

        // ================================================================== idle life: sleep, random quirks, self-talk

        private void StepIdleLife()
        {
            if (_pendingTrick != null && !_dragging)
            {
                string next = _pendingTrick;
                _pendingTrick = null;
                RunTrick(next);
                return;
            }
            if (_clothOff) return;                       // the cloth game decides what he does
            if (_dragging || _state != "idle") return;
            if (_awayFromSpot && _visitTag == null && _bubble is not { IsVisible: true }) { WalkHome(); return; }
            if (_awayFromSpot) return;
            if (RestPose != null && _bubble is not { IsVisible: true } && _player.Has(RestPose)) { Play(RestPose); return; }
            if (DateTime.Now - LastActivity > SleepAfter) { Play("sleep"); return; }
            if (FocusMode || ChatOpen || _settings.QuirkFrequency == "Off" || DateTime.Now < _nextQuirk) return;
            ScheduleNextQuirk();
            DoRandomQuirk();
        }

        private void ScheduleNextQuirk()
        {
            (int min, int max) = _settings.QuirkFrequency switch
            {
                "Often" => (30, 60),
                "Rarely" => (300, 600),
                _ => (60, 180),
            };
            _nextQuirk = DateTime.Now.AddSeconds(_rng.Next(min, max));
        }

        private void DoRandomQuirk()
        {
            // Weighted pick. Self-talk is ~15% so it happens a few times an hour.
            var options = new (string id, int weight)[]
            {
                ("shake_head", 10), ("jump", 8), ("stretch_yawn", 10), ("tail_chase", 7), ("wave", 8),
                ("look_roar", 9), ("walk_around", 12), ("butterfly", 8), ("chase", 4), ("talk", _settings.SelfTalk ? 14 : 0),
                ("untie", Buddy.Has("cloth") ? 9 : 0),
            };
            int total = 0;
            foreach (var o in options) total += o.weight;
            int roll = _rng.Next(total);
            foreach (var (id, weight) in options)
            {
                if ((roll -= weight) >= 0) continue;
                if (id == "talk") { if (_bubble is not { IsVisible: true }) Say(RandomLine()); }
                else DoTrick(id);
                return;
            }
        }

        private string RandomLine()
        {
            string name = _settings.NameOrDefault;
            int hour = DateTime.Now.Hour;
            string greeting = hour < 12 ? "Good morning! ☀️" : hour < 17 ? "Good afternoon! 🌤️" : "Good evening! 🌙";
            // Lines with a topic are skipped while an extras pack covers it (B5), so reminders aren't doubled.
            var lines = new System.Collections.Generic.List<string>();
            foreach (var (topic, line) in new (string?, string)[]
            {
                (null, greeting),
                (null, $"{name} here! Need a hand? Click me."),
                (null, "*yawn* … long day?"),
                ("water", "Don't forget to drink some water 💧"),
                ("stretch", $"Time to stretch? I just did! {Buddy.Emoji}"),
                (null, "I'm right here if you need me."),
                (null, "Ask me anything, I'm a good listener."),
                ("break", "Have you taken a break lately?"),
                (null, "This taskbar is very comfy."),
                (null, Buddy.Hello),
                (null, "Need help with some code? Right-click me → Open Claude Code."),
                (null, "*purrs*"),
            })
                if (topic == null || !SuppressedTopics.Contains(topic)) lines.Add(line);
            return lines[_rng.Next(lines.Count)];
        }

        // ================================================================== walking: leave / arrive / stroll

        private void StartStroll()
        {
            double range = _ground.Right - _ground.Left;
            double half = EdgeHalfPx();
            double dist = range * (0.08 + _rng.NextDouble() * 0.14);
            int dir = _rng.Next(2) == 0 ? -1 : 1;
            double target = _centerPx + dir * dist;
            if (target < _ground.Left + half || target > _ground.Right - half) { dir = -dir; target = _centerPx + dir * dist; }
            target = Math.Clamp(target, _ground.Left + half, _ground.Right - half);
            _mode = Mode.Strolling;
            StandFacing(dir, () => BeginWalk(target));
        }

        private void StepWalk()
        {
            double speed = WalkSpeedPx * (_mode == Mode.Strolling ? _walkSpeedMul : 1);
            double total = Math.Abs(_walkTargetPx - _walkFromPx);
            double seconds = _mode == Mode.Strolling ? Math.Max(0.5, total / speed) : WalkSeconds;
            double u = Math.Min(1, (DateTime.Now - _walkStart).TotalSeconds / seconds);
            _centerPx = _walkFromPx + (_walkTargetPx - _walkFromPx) * u;

            if (_mode == Mode.Leaving)
            {
                _alpha = (byte)(255 * Math.Clamp((1 - u) / 0.45, 0, 1));
                if (u >= 1)
                {
                    _walking = false;
                    _mode = Mode.Away;
                    _clock.Stop();
                    _surface.Hide();
                    _alpha = 255;
                    _flip = false;
                }
            }
            else if (_mode == Mode.Arriving)
            {
                _alpha = (byte)(255 * Math.Clamp(u / 0.4, 0, 1));
                if (u >= 1) { _alpha = 255; _mode = Mode.Strolling; SitDown(Touch); }
            }
            else if (u >= 1)
            {
                _walkSpeedMul = 1;
                if (_homing) { _homing = false; _awayFromSpot = false; SitDown(); }
                else if (_awayFromSpot)
                {
                    string? trick = _visitTrick;   // arrived for a reminder: sit, then do its trick
                    _visitTrick = null;
                    SitDown(() => { if (trick != null && _player.Has(trick) && trick is not ("walk_around" or "chase" or "butterfly")) Play(trick, () => Play("idle")); });
                }
                else { SaveSpot(); SitDown(); }
            }
        }

        // ================================================================== chase the pointer

        private void StartChase()
        {
            _mode = Mode.Chasing;
            _modeStart = DateTime.Now;
            Native.GetCursorPos(out var p);
            StandFacing(p.X >= _centerPx ? 1 : -1, () =>
            {
                if (_pendingTrick != null) { _mode = Mode.Strolling; SitDown(); return; }   // interrupted while standing up
                Play("walk");
                _walking = true;
            });
        }

        private void StepChase()
        {
            if (!_walking) return;   // still turning / standing up
            if ((DateTime.Now - _modeStart).TotalSeconds > ChaseSeconds) { EndChase(); return; }
            Native.GetCursorPos(out var p);
            double half = EdgeHalfPx();
            double target = Math.Clamp(p.X, _ground.Left + half, _ground.Right - half);
            double dx = target - _centerPx;
            double step = WalkSpeedPx * 2.2 / 15;
            if (Math.Abs(dx) > step)
            {
                _flip = dx < 0;
                _centerPx += Math.Sign(dx) * step;
                _frameStep = 2;              // faster legs to match the quicker pace
            }
            else
            {
                _frameStep = 0;              // caught up: hold still
            }
        }

        private void EndChase()
        {
            _walking = false;
            _frameStep = 1;
            SaveSpot();
            _mode = Mode.Strolling;
            SitDown(Touch);
        }

        // ================================================================== butterfly game

        private void StartButterfly()
        {
            _mode = Mode.Playing;
            _modeStart = DateTime.Now;
            _playStep = 0;
            int bpx = (int)Math.Round(56 * _ground.Scale);
            _butterflySprites.SetDecodeWidth(bpx);
            _butterfly.EnsureSize(_butterflySprites.DecodeWidth, _butterflySprites.DecodeWidth);
            // Enter from the side with more room.
            bool fromRight = _centerPx < (_ground.Left + _ground.Right) / 2.0;
            _bfX = fromRight ? Math.Min(_ground.Right, _centerPx + 600 * _ground.Scale) : Math.Max(_ground.Left, _centerPx - 600 * _ground.Scale);
            _bfY = HeadTopPx() - 160 * _ground.Scale;
            _butterfly.Show();
        }

        private double HeadTopPx() => BodyBoundsPx.Top;

        private void StepButterfly()
        {
            double t = (DateTime.Now - _modeStart).TotalSeconds;
            double s = _ground.Scale;
            double hx = _centerPx, hy = HeadTopPx() - 30 * s;   // point just above his head
            (double tx, double ty) target;

            if (t < 2.4) target = (hx + 70 * s, hy - 20 * s);                                   // flutter in
            else if (t < 6.0) { double a = (t - 2.4) * 2.2; target = (hx + Math.Cos(a) * 75 * s, hy - 25 * s + Math.Sin(a) * 30 * s); } // circle his head
            else if (t < 6.6) target = (hx + 30 * s, hy + 10 * s);                              // comes close…
            else if (t < 7.2) target = (hx + 110 * s, hy - 70 * s);                             // …dodges
            else if (t < 7.6) target = (hx + 25 * s, hy + 5 * s);
            else target = (hx - 900 * s, hy - 500 * s);                                         // flies away

            double ease = t < 7.6 ? 0.18 : 0.06;
            double nx = _bfX + (target.tx - _bfX) * ease;
            double ny = _bfY + (target.ty - _bfY) * ease + Math.Sin(t * 9) * 2.5 * s;
            if (Math.Abs(nx - _bfX) > 0.5) _bfFlip = nx < _bfX;
            _bfX = nx; _bfY = ny;

            // The lion's part of the game.
            if (_playStep == 0 && t > 1.4) { _playStep = 1; Play("look_up"); }
            if (_playStep == 1 && t > 6.3) { _playStep = 2; Play("swat", () => Play("look_up")); }
            if (_playStep == 2 && t > 7.4) { _playStep = 3; Play("swat", () => Play("happy", () => Play("idle"))); }
            if (t > 10.5)
            {
                _butterfly.Hide();
                _mode = Mode.Sitting;
                Play("idle");
                return;
            }
            DrawButterfly();
        }

        private void DrawButterfly()
        {
            var anim = _butterflySprites["butterfly"];
            _butterflyFrame = (_butterflyFrame + 1) % anim.Frames;
            int n = _butterflySprites.DecodeWidth;
            _butterfly.Clear();
            _butterfly.Blit(_butterflySprites.Frame("butterfly", _butterflyFrame), n, n, 0, 0, _bfFlip, n);
            _butterfly.Push((int)(_bfX - n / 2.0), (int)(_bfY - n / 2.0));
            _butterfly.KeepOnTop();
        }

        // ================================================================== B1: licking the pointer

        private System.Drawing.Point _lickLast;
        private int _lickStill;
        private DateTime _lickAfter = DateTime.MinValue;

        /// <summary>Head centre in screen px while sitting (from the idle frame's framing).</summary>
        private (double X, double Y, double R) HeadPx()
        {
            var idle = _player["idle"];
            double top = _ground.Y + 4 * _ground.Scale - idle.BaselineY * _px;
            return (_centerPx, top + 0.40 * _px, 0.2 * _px);
        }

        /// <summary>When the pointer rests near his face (~0.5 s), he licks it, turning toward it.</summary>
        private void StepLick()
        {
            if (!Buddy.Has("lick") || _dragging || _state != "idle" || DateTime.Now < _lickAfter || _bubble is { IsVisible: true }) { _lickStill = 0; return; }
            Native.GetCursorPos(out var p);
            var cur = new System.Drawing.Point(p.X, p.Y);
            _lickStill = Math.Abs(cur.X - _lickLast.X) + Math.Abs(cur.Y - _lickLast.Y) <= 3 * _ground.Scale ? _lickStill + 1 : 0;
            _lickLast = cur;
            var (hx, hy, hr) = HeadPx();
            double dx = p.X - hx, dy = hy - p.Y, d = Math.Sqrt(dx * dx + dy * dy);
            if (d > hr * 2.6 || _lickStill < 7) return;
            string dir;
            if (d < hr * 0.85) dir = "front";
            else
            {
                double a = Math.Atan2(dy, dx) * 180 / Math.PI;
                dir = a is >= 20 and < 65 ? "upright" : a is >= 65 and < 115 ? "up" : a is >= 115 and < 160 ? "upleft" : dx < 0 ? "left" : "right";
            }
            if (!_player.Has("lick_" + dir)) return;
            _lickStill = 0;
            _lickAfter = DateTime.Now.AddSeconds(15);
            Touch();
            Play("lick_" + dir, () => Play("idle"));
        }

        // ================================================================== B1: the cloth game
        // He gets the cloth off (untie trick). It lies beside him as its own little window. Pick it up and he runs
        // away from it; corner him and he dodges past (3 times, then he's tired); catch him and he gives in and holds
        // out his paw; drop the cloth by his paw and he ties it back on. Left alone, he fetches it after a while.
        // Anything else (chat, a trick, dragging him, sending him away) puts the cloth straight back on.

        private const double ClothFetchAfterSeconds = 150;
        private const int MaxDodges = 3, DodgeGraceTicks = 12;
        // Measured from the cat's 360 px frames (fractions of the frame): where the untie drops the cloth, and the
        // cloth sprite's heap centre when lying (frame 1) and when held (frame 2).
        private const double DropX = 225 / 360.0, DropY = 294 / 360.0;
        private const double LyingX = 183 / 360.0, LyingY = 273.6 / 360.0, HeldX = 186 / 360.0, HeldY = 242.4 / 360.0;

        private bool _clothOff;           // the cloth is off his paw (the game is on)
        private LayeredSurface? _clothWin;
        private SpritePlayer? _clothSprites;
        private double _clothX, _clothY, _clothVy;   // the heap's centre on screen (px)
        private bool _clothHeld, _clothCaught, _clothArmed, _clothEscaped;
        private int _clothDodges, _clothGrace;
        private DateTime _clothLeftAlone;
        private double _dodgeFrom, _dodgeTo;
        private (int, int, bool) _clothDrawn;

        private void TryUntie(bool? force = null)
        {
            if (!Buddy.Has("cloth") || !_player.Has("untie_success")) { Play("idle"); return; }
            bool success = force ?? _rng.Next(3) == 0;             // about 1 in 3: success feels special
            Play(success ? "untie_success" : "untie_giveup", success ? StartClothGame : () => Play("idle"));
        }

        // Lying cloth: on the taskbar's top edge (a little above the paw line, so it isn't half inside the taskbar,
        // where clicks would go to the taskbar instead).
        private double ClothRestY() => _ground.Y + 4 * _ground.Scale - _player["idle"].BaselineY * _px + DropY * _px - 0.03 * _px;
        private double DropPointX() => _centerPx - _player["idle"].CenterX * _px + DropX * _px;
        // By his paw: near the spot the cloth falls from, kept inside the screen (at the edge it may be just off it).
        private bool ClothNearPaw() =>
            Math.Abs(_clothX - Math.Clamp(DropPointX(), _ground.WorkArea.Left + 0.05 * _px, _ground.WorkArea.Right - 0.05 * _px)) < 0.13 * _px;

        private void StartClothGame()
        {
            _clothOff = true;
            _clothSprites ??= new SpritePlayer(Buddy);
            _clothSprites.SetDecodeWidth(_px);
            if (_clothWin == null) { _clothWin = new LayeredSurface("AssistailCloth", ClothWndProc); }
            _clothWin.EnsureSize(_clothSprites.DecodeWidth, _clothSprites.DecodeWidth);
            _clothX = DropPointX(); _clothY = ClothRestY(); _clothVy = 0;
            _clothHeld = _clothCaught = _clothArmed = _clothEscaped = false;
            _clothDodges = 0;
            _clothLeftAlone = DateTime.Now;
            _clothDrawn = default;
            _clothWin.Show();
            DrawCloth();
            App.Log($"Cloth game starts: cloth at ({_clothX:0},{_clothY:0}), buddy at {_centerPx:0}");
            Play("idle");                                          // → idle_bare
            Say("Ha, got it off! 😼 Try to put it back on me…", 8, "cloth", talk: false);
        }

        /// <summary>Ends the game at once (the cloth is back on his paw, no animation).</summary>
        private void EndClothGame([System.Runtime.CompilerServices.CallerMemberName] string why = "")
        {
            if (!_clothOff) return;
            App.Log($"Cloth game ends ({why}, state {_state})");
            _clothOff = false;
            if (_clothHeld) { _clothHeld = false; Native.ReleaseCapture(); }
            _clothWin?.Hide();
            if (_mode == Mode.ClothPlay) { _mode = Mode.Sitting; _flip = false; SaveSpot(); }
            if (_bubble is { IsVisible: true } && _bubbleTag == "cloth") _bubble.Dismiss();
        }

        private void DrawCloth()
        {
            if (_clothWin == null || _clothSprites == null) return;
            int n = _clothSprites.DecodeWidth;
            double ox = _clothHeld ? HeldX : LyingX, oy = _clothHeld ? HeldY : LyingY;
            var sig = ((int)Math.Round(_clothX), (int)Math.Round(_clothY), _clothHeld);
            if (sig == _clothDrawn) { return; }
            _clothDrawn = sig;
            _clothWin.Clear();
            _clothWin.Blit(_clothSprites.Frame("cloth", _clothHeld ? 1 : 0), n, n, 0, 0, false, n);
            _clothWin.Push((int)Math.Round(_clothX - ox * n), (int)Math.Round(_clothY - oy * n));
            _clothWin.KeepOnTop();
        }

        private void StandFacingBare(int dir, Action then) { _flip = dir < 0; Play("turn", () => Play("stand_up", then)); }   // → *_bare

        private void SitDownBare(Action? then = null)
        {
            _mode = Mode.ClothPlay;
            Play("stand_up", () => Play("turn", () => { _flip = false; _mode = Mode.Sitting; SaveSpot(); Play("idle"); then?.Invoke(); }, reverse: true), reverse: true);
        }

        private int _clothTopTick;

        private void StepCloth()
        {
            if (_clothWin == null) return;
            if (++_clothTopTick >= 15) { _clothTopTick = 0; _clothWin.KeepOnTop(); }   // above the taskbar and him
            double half = EdgeHalfPx(running: true);
            double left = _ground.Left + half, right = _ground.Right - half;
            double rest = ClothRestY();
            if (!_clothHeld && _clothY < rest) { _clothVy += 2.2 * _ground.Scale; _clothY = Math.Min(rest, _clothY + _clothVy); }
            if (_clothGrace > 0) _clothGrace--;
            bool settled = !_clothHeld && _clothY >= rest - 0.5;
            double gap = Math.Abs(_clothX - _centerPx);
            int away = _clothX > _centerPx ? -1 : 1;
            bool cornered = (away < 0 && _centerPx <= left + 1) || (away > 0 && _centerPx >= right - 1);
            double catchR = 0.26 * _px, fleeR = 1.0 * _px, safeR = 1.45 * _px;
            if (_clothHeld && gap > catchR + 0.2 * _px) _clothEscaped = true;
            bool canCatch = _clothHeld && !_clothCaught && _clothGrace <= 0 && _clothEscaped && gap < catchR;

            if (_mode == Mode.Sitting && _state == "idle_bare" && !_dragging)
            {
                if (settled && !_clothArmed && ClothNearPaw() && (DateTime.Now - _clothLeftAlone).TotalSeconds > ClothFetchAfterSeconds)
                    _clothArmed = true;                                         // left alone long enough: ties it on himself
                if (settled && _clothArmed && ClothNearPaw())
                {
                    _clothWin.Hide();
                    if (_bubble is { IsVisible: true } && _bubbleTag == "cloth") _bubble.Dismiss();
                    App.Log("Cloth: retie");
                    Play("retie", () => { _clothOff = false; Play("idle"); });
                    return;
                }
                if (_clothHeld && !_clothCaught)
                {
                    if (canCatch || (cornered && _clothGrace <= 0 && (_clothDodges >= MaxDodges || gap < catchR))) { GiveIn(); Play("caught_bare", () => Play("idle")); }
                    else if (gap < fleeR && (!cornered || _clothDodges < MaxDodges)) { _mode = Mode.ClothPlay; StandFacingBare(away, () => Play("run_bare")); }
                }
                else if (settled && !ClothNearPaw() && (DateTime.Now - _clothLeftAlone).TotalSeconds > ClothFetchAfterSeconds)
                {
                    _mode = Mode.ClothPlay;
                    StandFacingBare(_clothX - (DropPointX() - _centerPx) > _centerPx ? 1 : -1, () => Play("walk_bare"));
                }
            }
            else if (_mode == Mode.ClothPlay && _state == "run_bare")
            {
                _flip = away < 0;
                double step = WalkSpeedPx * 2.8 / 15;
                _centerPx = Math.Clamp(_centerPx + away * step, left, right);
                bool nowCornered = (away < 0 && _centerPx <= left + 1) || (away > 0 && _centerPx >= right - 1);
                if (canCatch) { GiveIn(); SitDownBare(() => Play("caught_bare", () => Play("idle"))); }
                else if (!_clothHeld || gap > safeR) SitDownBare();
                else if (nowCornered)
                {
                    if (_clothDodges < MaxDodges && gap > 0.12 * _px)
                    {
                        _clothDodges++;
                        App.Log($"Cloth: dodge {_clothDodges}");
                        _dodgeFrom = _centerPx;
                        _dodgeTo = Math.Clamp(_clothX - away * 0.5 * _px, left, right);   // lands past the cloth
                        _flip = -away < 0;
                        Play("dodge_bare", () => { _clothEscaped = true; _clothGrace = DodgeGraceTicks; if (_clothHeld) Play("run_bare"); else SitDownBare(); });
                    }
                    else { GiveIn(); SitDownBare(() => Play("caught_bare", () => Play("idle"))); }
                }
            }
            else if (_mode == Mode.ClothPlay && _state == "dodge_bare")
            {
                double u = Math.Clamp((_frame - 3) / 8.0, 0, 1);                   // airborne between frames 3 and 11
                _centerPx = _dodgeFrom + (_dodgeTo - _dodgeFrom) * (0.5 - 0.5 * Math.Cos(Math.PI * u));
            }
            else if (_mode == Mode.ClothPlay && _state == "walk_bare")              // fetching it himself
            {
                double target = Math.Clamp(_clothX - (DropPointX() - _centerPx), left, right);
                double dx = target - _centerPx, step = WalkSpeedPx / 15;
                _flip = dx < 0;
                if (_clothHeld) Play("run_bare");
                else if (Math.Abs(dx) <= step) { _centerPx = target; _clothArmed = true; SitDownBare(); }
                else _centerPx += Math.Sign(dx) * step;
            }
            DrawCloth();
        }

        private void GiveIn()
        {
            _clothCaught = true;
            App.Log($"Cloth: caught (dodges {_clothDodges}); paw drop point x={DropPointX():0}");
            Say("OK, OK… you got me. 🙀 Put it on my paw.", 8, "cloth", talk: false);
        }

        private const int CWM_SETCURSOR = 0x20, CWM_MOUSEMOVE = 0x200, CWM_LBUTTONDOWN = 0x201, CWM_LBUTTONUP = 0x202, CWM_CAPTURECHANGED = 0x215, CWM_MOUSEACTIVATE = 0x21;

        /// <summary>The cloth window: grab it, carry it anywhere, let go and it drops to the taskbar's edge.</summary>
        private IntPtr ClothWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case CWM_MOUSEACTIVATE: handled = true; return new IntPtr(3);   // MA_NOACTIVATE
                case CWM_SETCURSOR: Native.SetCursor(_handCursor); handled = true; return new IntPtr(1);
                case CWM_LBUTTONDOWN:
                    if (!_clothOff || _state == "retie") break;
                    _clothHeld = true;
                    _clothArmed = false;
                    App.Log("Cloth: picked up");
                    _clothGrace = DodgeGraceTicks;
                    if (!_clothCaught) { _clothEscaped = false; _clothDodges = 0; }
                    _clothLeftAlone = DateTime.Now;
                    Native.SetCapture(hwnd);
                    MoveHeldCloth();
                    if (_bubble is { IsVisible: true } && _bubbleTag == "cloth" && !_clothCaught) _bubble.Dismiss();
                    Touch();
                    handled = true;
                    break;
                case CWM_MOUSEMOVE:
                    if (_clothHeld) { MoveHeldCloth(); DrawCloth(); }
                    break;
                case CWM_LBUTTONUP:
                case CWM_CAPTURECHANGED when _clothHeld && lParam != hwnd:
                    if (!_clothHeld) break;
                    _clothHeld = false;
                    App.Log($"Cloth: let go at {_clothX:0} (buddy at {_centerPx:0}, caught {_clothCaught})");
                    _clothVy = 0;
                    _clothLeftAlone = DateTime.Now;
                    _clothArmed = _clothCaught && ClothNearPaw();
                    if (msg == CWM_LBUTTONUP) Native.ReleaseCapture();
                    DrawCloth();
                    handled = true;
                    break;
            }
            return IntPtr.Zero;
        }

        private void MoveHeldCloth()
        {
            Native.GetCursorPos(out var p);
            _clothX = Math.Clamp(p.X, _ground.WorkArea.Left, _ground.WorkArea.Right);
            _clothY = Math.Min(ClothRestY(), p.Y + 6 * _ground.Scale);
        }

        // ================================================================== speech bubble

        private SpeechBubble CreateBubble()
        {
            var b = new SpeechBubble();
            b.Clicked += () => BubbleClicked?.Invoke();
            b.RightClicked += () => BubbleRightClicked?.Invoke();
            b.Hidden += () =>
            {
                if (CanReact && _state == "talking") Play("idle");
                string? tag = _bubbleTag;
                if (tag != null && tag == _visitTag) _visitTag = null;   // he walks back once he's sitting
                _bubbleTag = null;
                BubbleHidden?.Invoke(tag);
            };
            return b;
        }

        private void PlaceBubble()
        {
            if (_bubble == null) return;
            var body = BodyBoundsPx;
            _bubble.PlaceAt((int)_centerPx, body.Top - (int)(6 * _ground.Scale), MonitorWorkAreaPx);
        }

        // ================================================================== drawing

        private (string, int, bool, int, int, byte, int) _lastDrawn;

        private void Render(bool force = false)
        {
            if (!_surface.Visible || _px <= 0) return;
            var idle = _player["idle"];
            var anim = _player[_state];
            int f = Math.Clamp(_frame, 0, anim.Frames - 1);
            int n = _px;

            // Skip the work entirely if nothing visible changed (held frames, paused chase, etc.).
            var sig = (_state, f, _flip, (int)Math.Round(_centerPx), (int)_dragLiftPx, _alpha, n);
            if (!force && sig == _lastDrawn) return;
            _lastDrawn = sig;
            if (Profile) { _profRenders++; _profWatch.Restart(); }
            try { RenderFrame(idle, anim, f, n); }
            finally { if (Profile) _profMs += _profWatch.Elapsed.TotalMilliseconds; }
        }

        private void RenderFrame(LionAnim idle, LionAnim anim, int f, int n)
        {
            _surface.Clear();

            double anchorX = idle.CenterX * n, anchorY = idle.BaselineY * n;
            double lift = (f < anim.Lift.Length ? anim.Lift[f] : 0) * anim.LiftPerUnit * n + _dragLiftPx;
            double sw = anim.ShadowW * n * Math.Max(0.45, 1 - lift / (n * 0.6));
            DrawShadow(anchorX, anchorY, sw / 2, sw * 0.11, Math.Max(0.25, 1 - lift / (n * 0.5)));

            int dx = (int)Math.Round((idle.CenterX - anim.CenterX) * n);
            int dy = (int)Math.Round((idle.BaselineY - anim.BaselineY) * n - _dragLiftPx);
            int w = _player.DecodeWidth;
            _surface.Blit(_player.Frame(_state, f), w, w, dx, dy, _flip, (int)Math.Round(2 * anchorX));

            int overlap = (int)Math.Round(4 * _ground.Scale);   // paws sit slightly "into" the taskbar edge
            _surface.Push((int)Math.Round(_centerPx - anchorX), (int)Math.Round(_ground.Y + overlap - anchorY), _alpha);
        }

        private void DrawShadow(double cx, double cy, double rx, double ry, double opacity)
        {
            int n = _px;
            uint[] buf = _surface.Buffer;
            int x0 = Math.Max(0, (int)(cx - rx)), x1 = Math.Min(n - 1, (int)(cx + rx));
            int y0 = Math.Max(0, (int)(cy - ry)), y1 = Math.Min(n - 1, (int)(cy + ry));
            for (int y = y0; y <= y1; y++)
            {
                double ny = (y + 0.5 - cy) / ry;
                for (int x = x0; x <= x1; x++)
                {
                    double nx = (x + 0.5 - cx) / rx;
                    double r = Math.Sqrt(nx * nx + ny * ny);
                    if (r >= 1) continue;
                    double a = r < 0.55 ? 0.38 - (0.20 * r / 0.55) : 0.18 * (1 - (r - 0.55) / 0.45);
                    buf[y * n + x] = (uint)(Math.Clamp(a * opacity, 0, 1) * 255) << 24;   // premultiplied black
                }
            }
        }

        // ================================================================== layout

        /// <summary>How close his centre may come to the ends of the taskbar. The lion: his shadow (as before). Pack
        /// buddies can be wider than their shadow (the cat sitting is ~0.2 of the frame each side), so all of him stays
        /// on screen; <paramref name="running"/>: on all fours, longer still.</summary>
        private double EdgeHalfPx(bool running = false) =>
            Math.Max(_player["idle"].ShadowW / 2, Buddy.IsBuiltIn ? 0 : running ? 0.32 : 0.22) * _px;

        private double TargetCenterPx()
        {
            double half = EdgeHalfPx();
            double x = _ground.Left + _settings.LionX * (_ground.Right - _ground.Left);
            return Math.Clamp(x, _ground.Left + half, _ground.Right - half);
        }

        private void SaveSpot()
        {
            _settings.LionX = (_centerPx - _ground.Left) / Math.Max(1, _ground.Right - _ground.Left);
            _settings.Save();
        }

        private void OnDisplayChanged(object? sender, EventArgs e) =>
            _dispatcher.BeginInvoke(() => Layout(force: true));   // SystemEvents fires on another thread

        private void Layout(bool force = false)
        {
            var g = TaskbarInfo.GetGround();
            double sizeDip = _settings.LionSize switch { "S" => 150, "L" => 240, _ => 190 };
            bool changed = force || g.Y != _ground.Y || g.Left != _ground.Left || g.Right != _ground.Right ||
                           Math.Abs(g.Scale - _ground.Scale) > 0.01 || sizeDip != _sizeDip;
            if (!changed) return;
            _ground = g;
            _sizeDip = sizeDip;
            _player.SetDecodeWidth((int)Math.Round(_sizeDip * _ground.Scale));
            _px = _player.DecodeWidth;
            _surface.EnsureSize(_px, _px);
            if (_mode == Mode.Sitting) _centerPx = TargetCenterPx();
            Render(force: true);
        }

        private void Housekeeping()
        {
            if (_mode != Mode.Sitting || _dragging) return;
            bool fullscreen = TaskbarInfo.IsFullscreenAppActive();
            if (fullscreen && !_hiddenForFullscreen) { _hiddenForFullscreen = true; _surface.Hide(); _bubble?.Dismiss(); return; }
            if (!fullscreen && _hiddenForFullscreen) { _hiddenForFullscreen = false; _surface.Show(); }
            Layout();
            _surface.KeepOnTop();   // the taskbar can come in front when clicked
            if (_clothOff) _clothWin?.KeepOnTop();   // the cloth stays above him, so clicks near his paw reach it
        }

        // ================================================================== mouse

        private const int WM_SETCURSOR = 0x20, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202,
                          WM_RBUTTONUP = 0x205, WM_MOUSELEAVE = 0x2A3, WM_CAPTURECHANGED = 0x215,
                          WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case Native.WM_POWERBROADCAST when wParam.ToInt32() == Native.PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero:
                    var setting = Marshal.PtrToStructure<Native.POWERBROADCAST_SETTING>(lParam);
                    if (setting.PowerSetting == Native.GUID_CONSOLE_DISPLAY_STATE)
                    {
                        _pausedDisplayOff = setting.Data == 0;     // 0 = off, 1 = on, 2 = dimmed
                        UpdatePause();
                    }
                    handled = true;
                    return new IntPtr(1);
                case WM_MOUSEACTIVATE:
                    // Clicking him must not steal focus: the chat (or whatever app) stays active,
                    // so a click can tell "chat is in front → hide it" from "chat is behind → bring it up".
                    handled = true;
                    return new IntPtr(MA_NOACTIVATE);
                case WM_SETCURSOR:
                    Native.SetCursor(_handCursor);
                    handled = true;
                    return new IntPtr(1);
                case WM_MOUSEMOVE:
                    if (!_tracking)
                    {
                        var tme = new Native.TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<Native.TRACKMOUSEEVENT>(), dwFlags = Native.TME_LEAVE, hwndTrack = hwnd };
                        _tracking = Native.TrackMouseEvent(ref tme);
                        OnHover();
                    }
                    OnMouseMove();
                    break;
                case WM_MOUSELEAVE:
                    _tracking = false;
                    break;
                case WM_LBUTTONDOWN:
                    OnMouseDown();
                    handled = true;
                    break;
                case WM_LBUTTONUP:
                    OnMouseUp();
                    handled = true;
                    break;
                case WM_CAPTURECHANGED:
                    if (_mouseDown && lParam != hwnd) OnMouseUp();
                    break;
                case WM_RBUTTONUP:
                    Touch();
                    Native.SetForegroundWindow(hwnd);   // so the menu closes when clicking elsewhere
                    MenuRequested?.Invoke();
                    handled = true;
                    break;
            }
            return IntPtr.Zero;
        }

        private void OnHover()
        {
            Touch();
            if (_clothOff) return;
            if (_mode == Mode.Sitting && !_dragging && _state is "idle" or "sleep")
                Play("hover", () => Play("idle"));
        }

        private void StopTrickForClick()
        {
            App.Log($"Click interrupts {_mode}");
            switch (_mode)
            {
                case Mode.Playing:                       // butterfly flies off, he settles
                    _butterfly.Hide();
                    _mode = Mode.Sitting;
                    Play("idle");
                    break;
                case Mode.Chasing:
                    if (_walking) EndChase();            // sits down (the chat opens meanwhile)
                    else _pendingTrick = "__sit";       // still standing up: sit back down instead of walking
                    break;
                case Mode.Strolling:
                    if (_walking) { _walkSpeedMul = 1; _homing = false; if (!_awayFromSpot) SaveSpot(); SitDown(); }
                    else _pendingTrick = "__sit";
                    break;
                // Arriving: let him finish walking in; the chat opens next to him.
            }
        }

        private void OnMouseDown()
        {
            // A click always wins: stop whatever trick is running and open the chat.
            if (_mode == Mode.ClothPlay) { Touch(); EndClothGame(); Clicked?.Invoke(); return; }
            if (_mode is Mode.Playing or Mode.Strolling or Mode.Chasing or Mode.Arriving)
            {
                Touch();
                _pendingTrick = null;
                StopTrickForClick();
                Clicked?.Invoke();
                return;
            }
            if (_mode != Mode.Sitting) return;
            Native.GetCursorPos(out var p);
            _mouseDown = true;
            _dragging = false;
            _downCursorX = p.X;
            _grabOffsetPx = p.X - _centerPx;
            Native.SetCapture(_surface.Hwnd);
        }

        private void OnMouseMove()
        {
            if (!_mouseDown) return;
            Native.GetCursorPos(out var p);
            if (!_dragging && Math.Abs(p.X - _downCursorX) > 5 * _ground.Scale)
            {
                _dragging = true;
                _dragLiftPx = 12 * _ground.Scale;
                _bubble?.Dismiss();
                Play("dragged");
            }
            if (_dragging)
            {
                double half = EdgeHalfPx();
                _centerPx = Math.Clamp(p.X - _grabOffsetPx, _ground.Left + half, _ground.Right - half);
                Render();
            }
        }

        private void OnMouseUp()
        {
            if (!_mouseDown) return;
            _mouseDown = false;
            Native.ReleaseCapture();
            Touch();
            if (_dragging)
            {
                _dragging = false;
                _dragLiftPx = 0;
                _awayFromSpot = _homing = false;
                _visitTag = null;
                SaveSpot();
                Play("landing", () => Play("idle"));
            }
            else
            {
                Clicked?.Invoke();
            }
        }
    }
}
