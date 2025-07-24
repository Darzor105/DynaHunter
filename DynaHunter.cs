using AOSharp.Common.GameData;
using AOSharp.Core;
using AOSharp.Core.Inventory;
using AOSharp.Core.Movement;
using AOSharp.Core.Misc;
using AOSharp.Core.GameData;
using AOSharp.Core.UI;
using System;
using System.Linq;
using System.Collections.Generic;

namespace DynaHunter
{
    public class DynaHunter : AOPluginEntry
    {
        private bool _huntingEnabled = false;
        private double _nextMoveTime = 0;
        private bool _debugMode = false;

        private Vector3 _lastPosition = Vector3.Zero;
        private double _lastPositionCheckTime = 0;
        private int _stuckCounter = 0;

        private const float MinMoveDistance = 1.0f;
        private const float AtLandmarkDistance = 5.0f;
        private const float StuckThreshold = 0.5f;
        private const int StuckLimit = 3;

        private (Vector3 pos, string name) _currentTargetLandmark;
        private bool _phasefrontCancelled = false;
        
        // Track visit timestamps for each camp to avoid consecutive visits
        private readonly Dictionary<int, double> _campVisitTimestamps = new Dictionary<int, double>();
        private int _currentCampIndex = -1;

        // Add a list of landmark names or positions to skip killing at if no dyna present
        private readonly HashSet<string> _skipKillLandmarks = new HashSet<string>
        {
            // Add landmark names or positions here that should skip killing if no Dyna
            // Example: "Dyna - Sandworm Nest",
            // Example: "Dyna - Deep Canyon"
            // For position-based skip, add logic below
        };

        private readonly List<(Vector3 pos, string name)> _rkLandmarks = new List<(Vector3, string)>
        {
            (new Vector3(1145.8f, 27.3f, 1158.7f), "Dyna - Core PW Mantis"),
            (new Vector3(1380, 25, 1935), "Dyna - Cyborg Camp A"),
            (new Vector3(1390, 25, 1950), "Dyna - Cyborg Camp B"),
            (new Vector3(3060.6f, 25.1f, 920.2f), "Dyna - Anun Camp A"),
            (new Vector3(1461.0f, 29.7f, 859.5f), "Dyna - Mantis Ridge"),
            (new Vector3(2662.5f, 25f, 2299.4f), "Dyna - Updated East Ridge"),
            (new Vector3(380, 25, 500), "Dyna - SW Anuns"),
            (new Vector3(380, 25, 900), "Dyna - SW Anuns 2"),
            (new Vector3(460, 25, 3140), "Dyna - Deep Canyon"),
            (new Vector3(700, 25, 2460), "Dyna - Canyon Ridge"),
            (new Vector3(1260, 25, 2860), "Dyna - Border Camp"),
            (new Vector3(1340, 25, 3060), "Dyna - Plateau Camp"),
            (new Vector3(420, 25, 1500), "Dyna - Sandworm Nest"),
            (new Vector3(2080, 25, 2770), "Dyna - Mid PW Mantis"),
            (new Vector3(2460, 25, 2660), "Dyna - High Mantis East"),
            (new Vector3(2740.4f, 26.3f, 2458.7f), "Dyna - Updated Ridge Extension"),
            (new Vector3(2980, 25, 2940), "Dyna - Overrust PW"),
            (new Vector3(3100, 25, 3260), "Dyna - Peak Camp"),
            (new Vector3(3520, 25, 2020), "Dyna - Far East Anuns"),
            (new Vector3(2220, 25, 3340), "Dyna - Elite Mantis"),
            (new Vector3(3940, 25, 1780), "Dyna - Sand Demon Boss"),
            (new Vector3(3460, 25, 2940), "Dyna - Cyborg Lair"),
            (new Vector3(3475, 25, 2980), "Dyna - Cyborg Overlord"),
            (new Vector3(1382.1f, 30.2f, 1937.4f), "Dyna - Cyborg Commander 1"),
            (new Vector3(1389.8f, 29.8f, 1952.1f), "Dyna - Cyborg Commander 2"),
            (new Vector3(3942.0f, 25.0f, 1778.9f), "Dyna - Updated PW Boss"),
            (new Vector3(3516.8f, 27.4f, 2019.5f), "Dyna - Replaced Updated Location"),
            (new Vector3(3079.7f, 32.7f, 1927.5f), "Dyna - Mantis Spire")
        };

        public override void Run()
        {
            Game.OnUpdate += OnUpdate;
            Chat.RegisterCommand("hunt", (cmd, args, window) => ToggleHunting());
            Chat.RegisterCommand("huntdebug", (cmd, args, window) => ToggleDebug());
            Chat.WriteLine("DynaHunter loaded. Use /hunt to start/stop. Use /huntdebug to toggle debug mode.");
        }

        public override void Teardown()
        {
            Game.OnUpdate -= OnUpdate;
        }

        private void ToggleHunting()
        {
            _huntingEnabled = !_huntingEnabled;
            if (_huntingEnabled)
                Chat.WriteLine("Hunting started.");
            else
            {
                MovementController.Instance?.Halt();
                DynelManager.LocalPlayer?.StopAttack();
                Chat.WriteLine("Hunting stopped.");
            }
        }

        private void ToggleDebug()
        {
            _debugMode = !_debugMode;
            Chat.WriteLine("Debug mode: " + (_debugMode ? "ON" : "OFF"));
        }

        private bool HasPhasefrontBuff()
        {
            var player = DynelManager.LocalPlayer;
            if (player?.Buffs == null) return false;
            return player.Buffs.Any(b => b?.Name != null &&
                (b.Name.Contains("Phasefront Wraith") || b.Name.Contains("Phasefront Banshee") || b.Name.Contains("Yalmaha")));
        }

        private void CancelPhasefrontBuff()
        {
            var player = DynelManager.LocalPlayer;
            if (player?.Buffs == null) return;

            var phasefrontBuff = player.Buffs.FirstOrDefault(b =>
                b?.Name != null &&
                (b.Name.Contains("Phasefront Wraith") ||
                 b.Name.Contains("Phasefront Banshee") ||
                 b.Name.Contains("Yalmaha")));

            if (phasefrontBuff != null)
            {
                phasefrontBuff.Remove();
                Chat.WriteLine($"Phasefront nano '{phasefrontBuff.Name}' removed from NCU.");
            }
        }

        // Y coordinate is now always 74 for flying
        private Vector3 GetSafeDestination(Vector3 dest, bool isLandmark)
        {
            Random rand = new Random();
            float xOffset = isLandmark ? ((float)rand.NextDouble() - 0.5f) * 2f : 0f;
            float zOffset = isLandmark ? ((float)rand.NextDouble() - 0.5f) * 2f : 0f;
            float safeY = 74f; // Always fly at altitude 74
            return new Vector3(dest.X + xOffset, safeY, dest.Z + zOffset);
        }

        private Vector3 Nudge(Vector3 pos)
        {
            Random rand = new Random();
            float angle = (float)(rand.NextDouble() * Math.PI * 2);
            float radius = 2.0f + (float)rand.NextDouble() * 2.0f;
            float dx = (float)Math.Cos(angle) * radius;
            float dz = (float)Math.Sin(angle) * radius;
            return new Vector3(pos.X + dx, pos.Y, pos.Z + dz);
        }

        private void SafeMoveTo(Vector3 destination)
        {
            var player = DynelManager.LocalPlayer;
            if (player == null)
                return;
            if (Vector3.Distance(player.Position, destination) > MinMoveDistance)
                MovementController.Instance?.SetDestination(destination);
            else if (_debugMode)
                Chat.WriteLine($"Skipping move: destination {destination} is too close to current position {player.Position}.");
        }

        private float GetPlayerMoveSpeed()
        {
            // AOSharp does not expose RunSpeed property directly on LocalPlayer.
            // Solution: Use a reasonable default.
            return 7.0f;
        }

        private (Vector3 pos, string name, int index) GetNextCamp(Vector3 playerPosition)
        {
            // Find the camp with the oldest visit timestamp, excluding the current camp
            int bestCampIndex = -1;
            double oldestTimestamp = double.MaxValue;
            
            for (int i = 0; i < _rkLandmarks.Count; i++)
            {
                // Skip the current camp to avoid consecutive visits
                if (i == _currentCampIndex)
                    continue;
                    
                // Get the last visit timestamp for this camp (0 if never visited)
                double lastVisit = _campVisitTimestamps.GetValueOrDefault(i, 0);
                
                if (lastVisit < oldestTimestamp)
                {
                    oldestTimestamp = lastVisit;
                    bestCampIndex = i;
                }
            }
            
            // If no valid camp found (shouldn't happen), fall back to nearest excluding current
            if (bestCampIndex == -1)
            {
                bestCampIndex = _rkLandmarks
                    .Select((landmark, index) => new { landmark, index })
                    .Where(x => x.index != _currentCampIndex)
                    .OrderBy(x => Vector3.Distance(playerPosition, x.landmark.pos))
                    .FirstOrDefault()?.index ?? 0;
            }
            
            var selectedCamp = _rkLandmarks[bestCampIndex];
            return (selectedCamp.pos, selectedCamp.name, bestCampIndex);
        }

        private bool IsDynaPresentNearLandmark(Vector3 landmarkPos, float radius = 40.0f)
            // Check for boss NPCs or mobs with high health near the landmark
            return DynelManager.NPCs?.Any(m =>
                m != null &&
                !m.IsPet &&
                m.Name != null &&
                m.Health > 0 &&
                Vector3.Distance(m.Position, landmarkPos) < radius
            ) ?? false;
        }

        private void OnUpdate(object sender, float deltaTime)
        {
            var player = DynelManager.LocalPlayer;
            if (!_huntingEnabled || player == null || !player.IsAlive)
                return;

            // Stuck detection
            if (Time.NormalTime - _lastPositionCheckTime > 1.0)
            {
                if (Vector3.Distance(player.Position, _lastPosition) < StuckThreshold)
                {
                    _stuckCounter++;
                    if (_debugMode)
                        Chat.WriteLine($"Stuck detected at {player.Position}. Counter: {_stuckCounter}");
                }
                else
                {
                    _stuckCounter = 0;
                }
                _lastPosition = player.Position;
                _lastPositionCheckTime = Time.NormalTime;
            }

            if (Time.NormalTime < _nextMoveTime)
                return;

            // Get next camp using new logic to avoid consecutive visits
            var nextCamp = GetNextCamp(player.Position);
            Vector3 landmarkDest = GetSafeDestination(nextCamp.pos, true);

            if (nextCamp.pos != Vector3.Zero)
            {
                float distToLandmark = Vector3.Distance(player.Position, landmarkDest);
                float distToCombatPos = Vector3.Distance(player.Position, nextCamp.pos);

                // New distance-based Phasefront removal: cancel when within 30 meters of combat destination
                if (HasPhasefrontBuff() && distToCombatPos <= 30.0f)
                {
                    CancelPhasefrontBuff();
                    _phasefrontCancelled = true;
                    if (_debugMode)
                        Chat.WriteLine($"Phasefront removed - within 30m of combat destination: {nextCamp.name}");
                }

                // After cancel, run rest of the way to the landmark
                if (_phasefrontCancelled && distToLandmark > AtLandmarkDistance)
                {
                    SafeMoveTo(landmarkDest);
                    if (_debugMode)
                        Chat.WriteLine($"Running to landmark after nano cancel: {nextCamp.name} ({landmarkDest})");
                    _nextMoveTime = Time.NormalTime + 2.0;
                    return;
                }
                else if (distToLandmark <= AtLandmarkDistance)
                {
                    // Update current camp and visit timestamp
                    _currentCampIndex = nextCamp.index;
                    _campVisitTimestamps[nextCamp.index] = Time.NormalTime;
                    _phasefrontCancelled = false;

                    // SKIP killing if landmark is in skip list and no dyna is present
                    bool skipKill = _skipKillLandmarks.Contains(nextCamp.name) && !IsDynaPresentNearLandmark(nextCamp.pos);

                    if (skipKill)
                    {
                        if (_debugMode)
                            Chat.WriteLine($"Skipping kill at {nextCamp.name} - no Dyna present.");
                        _nextMoveTime = Time.NormalTime + 2.0;
                        return;
                    }

                    // Otherwise, attack nearby mob if present
                    var nearbyMobs = DynelManager.NPCs?.Where(m =>
                        m != null &&
                        m.Position.DistanceFrom(player.Position) < 40 &&
                        !m.IsPet &&
                        m.Name != null &&
                        m.Health > 0).OrderByDescending(m => m.MaxHealth).ToList();

                    if (nearbyMobs?.Any() == true)
                    {
                        var target = nearbyMobs.First();
                        try
                        {
                            if (_debugMode)
                                Chat.WriteLine($"Attacking target: {target.Name} ({target.Health}/{target.MaxHealth})");
                            DynelManager.LocalPlayer?.Attack(target);
                        }
                        catch (Exception ex)
                        {
                            Chat.WriteLine($"Attack failed: {ex.Message}");
                        }
                    }
                    _nextMoveTime = Time.NormalTime + 2.0;
                    return;
                }

                // If still flying, keep moving toward landmark
                if (!_phasefrontCancelled && distToLandmark > MinMoveDistance)
                {
                    SafeMoveTo(landmarkDest);
                    if (_debugMode)
                        Chat.WriteLine($"Flying to: {nextCamp.name} ({landmarkDest})");
                    _nextMoveTime = Time.NormalTime + 2.0;
                    return;
                }
            }

            _nextMoveTime = Time.NormalTime + 2.0;
        }
    }
}
