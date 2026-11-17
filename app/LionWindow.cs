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
        private enum Mode { Sitting, Leaving, Away, Arriving, Strolling, Chasing, Playing }

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
        private readonly SpritePlayer _player = new();
        private readonly SpritePlayer _butterflySprites = new();
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

        public event Action? Clicked;
        public event Action? MenuRequested;

        public LionWindow(Settings settings)
        {
            _settings = settings;
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
            _surface.Show();
            Render(force: true);
            _clock.Start();
        }

        public void UpdateName() { /* the name is shown in the tray tooltip, chat, menus and speech */ }
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
                default: if (_player.Has(id)) Play(id, () => Play("idle")); break;
            }
        }

        /// <summary>Shows text in a speech bubble above his head while he "talks".</summary>
        public void Say(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || _mode is Mode.Away or Mode.Leaving) return;
            _bubble ??= CreateBubble();
            _bubble.Say(text.Trim());
            PlaceBubble();
            if (CanReact) Play("talking");
        }

        public void Leave()
        {
            if (_mode is Mode.Strolling or Mode.Chasing or Mode.Playing) { _pendingTrick = "__leave"; InterruptTrick(); return; }
            if (_mode != Mode.Sitting || _dragging) return;
            _bubble?.Dismiss();
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
            _bubble?.Close();
            _butterfly.Dispose();
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
                    SaveSpot();
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
            if (_dragging || _state != "idle") return;
            if (DateTime.Now - LastActivity > SleepAfter) { Play("sleep"); return; }
            if (ChatOpen || _settings.QuirkFrequency == "Off" || DateTime.Now < _nextQuirk) return;
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
            };
            int total = 0;
            foreach (var o in options) total += o.weight;
            int roll = _rng.Next(total);
            foreach (var (id, weight) in options)
            {
                if ((roll -= weight) >= 0) continue;
                if (id == "talk") Say(RandomLine());
                else DoTrick(id);
                return;
            }
        }

        private string RandomLine()
        {
            string name = _settings.NameOrDefault;
            int hour = DateTime.Now.Hour;
            string greeting = hour < 12 ? "Good morning! ☀️" : hour < 17 ? "Good afternoon! 🌤️" : "Good evening! 🌙";
            string[] lines =
            {
                greeting,
                $"{name} here! Need a hand? Click me.",
                "*yawn* … long day?",
                "Don't forget to drink some water 💧",
                "Time to stretch? I just did! 🦁",
                "I'm right here if you need me.",
                "Ask me anything, I'm a good listener.",
                "Have you taken a break lately?",
                "This taskbar is very comfy.",
                "Rawr! (that's lion for hello)",
                "Need help with some code? Right-click me → Open Claude Code.",
                "*purrs*",
            };
            return lines[_rng.Next(lines.Length)];
        }

        // ================================================================== walking: leave / arrive / stroll

        private void StartStroll()
        {
            double range = _ground.Right - _ground.Left;
            double half = _player["idle"].ShadowW / 2 * _px;
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
            double speed = WalkSpeedPx;
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
                SaveSpot();
                SitDown();
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
            double half = _player["idle"].ShadowW / 2 * _px;
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

        // ================================================================== speech bubble

        private SpeechBubble CreateBubble()
        {
            var b = new SpeechBubble();
            b.Hidden += () => { if (CanReact && _state == "talking") Play("idle"); };
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

        private double TargetCenterPx()
        {
            double half = _player["idle"].ShadowW / 2 * _px;
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
        }

        // ================================================================== mouse

        private const int WM_SETCURSOR = 0x20, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202,
                          WM_RBUTTONUP = 0x205, WM_MOUSELEAVE = 0x2A3, WM_CAPTURECHANGED = 0x215;

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
                    if (_walking) { SaveSpot(); SitDown(); }
                    else _pendingTrick = "__sit";
                    break;
                // Arriving: let him finish walking in; the chat opens next to him.
            }
        }

        private void OnMouseDown()
        {
            // A click always wins: stop whatever trick is running and open the chat.
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
                double half = _player["idle"].ShadowW / 2 * _px;
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
