using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using WKRace.Protocol;

namespace WKRace.Mod
{
    // Spectating inside the game: the local player becomes a spectator in a world built from the watched match's seed.
    //
    // The game loads and unloads levels around the local player's body and generates more as it climbs, so the body
    // goes wherever the watched player is: hidden (its hands are not drawn), out of play (no collisions, so it never
    // sets off a trigger, picks anything up or presses anything), unable to die, and with its own input locked. The
    // camera is either the watched player's eyes (first person, with their hands) or behind them (third person: the
    // mouse turns around them, the wheel moves closer or further). Left click follows the next player, right click the
    // one before; the view key switches between first and third person.
    //
    // What is seen comes from the positions the players send about 15 times a second, smoothed in between, in a world
    // generated from the same seed: close to what they see, not exactly (levels events of their own world, such as
    // doors or the goo, can be at another stage here).
    internal sealed class Spectator
    {
        private const float MinDistance = 1.5f;
        private const float MaxDistance = 12f;
        private const float OrbitSpeed = 3f;

        private static readonly AccessTools.FieldRef<ENT_Player, float> CamY = SafeRef("camY");

        private readonly RaceSession _session;
        private byte _racer;
        private bool _firstPerson;
        private float _orbitYaw;
        private float _orbitPitch = 15f;
        private float _distance = 4f;
        private bool _bodyReady;
        // Where the camera sits on the body at rest (third person moves it away every frame).
        private Vector3 _camRest;
        private readonly List<Renderer> _hiddenHands = new List<Renderer>();

        public Spectator(RaceSession session, byte racer, bool firstPerson)
        {
            _session = session;
            _racer = racer;
            _firstPerson = firstPerson;
        }

        public byte Racer { get { return _racer; } }
        public bool FirstPerson { get { return _firstPerson; } }

        public Rival Watched
        {
            get
            {
                Rival r;
                return _session.Rivals.TryGetValue(_racer, out r) ? r : null;
            }
        }

        public string WatchedName { get { Rival r = Watched; return r != null ? r.Name : ""; } }

        // The players that can be followed: still running, in racer order. The one watched stays in the list while
        // it is shown (a player who just died is held for a moment before moving on).
        private List<Rival> Followable()
        {
            var list = new List<Rival>();
            foreach (Rival r in _session.Rivals.Values)
                if (!r.IsOut && !r.Finished && r.Racer != _session.YourRacer) list.Add(r);
            list.Sort((a, b) => a.Racer.CompareTo(b.Racer));
            return list;
        }

        public void Next(int step)
        {
            List<Rival> list = Followable();
            if (list.Count == 0) return;
            int at = list.FindIndex(r => r.Racer == _racer);
            int next = at < 0 ? 0 : ((at + step) % list.Count + list.Count) % list.Count;
            Follow(list[next].Racer);
        }

        public void Follow(byte racer)
        {
            if (racer == _racer) return;
            _racer = racer;
            Rival r = Watched;
            if (r != null) _orbitYaw = r.Ghost.Yaw;
        }

        public void ToggleView()
        {
            _firstPerson = !_firstPerson;
        }

        // Moves on when the one watched is out: to the leader (the one with the most levels).
        public void FollowLeaderIfOut()
        {
            Rival r = Watched;
            if (r != null && !r.IsOut && !r.Finished) return;
            Rival best = null;
            foreach (Rival c in Followable())
                if (best == null || c.Splits.Count > best.Splits.Count) best = c;
            if (best != null) Follow(best.Racer);
        }

        // Takes the local player out of play.
        public void Enter()
        {
            ENT_Player p = ENT_Player.playerObject;
            if (p == null) return;
            try
            {
                if (p.dead) p.Revive();
                ObjectTagger tagger = p.GetTagger();
                if (tagger != null) tagger.RemoveTag("Dead");
                p.Lock();
                p.SetGodMode(true);
                p.noclip = true;
                _camRest = p.camTransform.localPosition;
                HideHands(p);
                _bodyReady = true;
                Rival r = Watched;
                if (r != null) _orbitYaw = r.Ghost.Yaw;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not make the player a spectator: " + e);
            }
        }

        // Back to normal (the world is about to be left anyway; this keeps a menu or a result screen usable).
        public void Exit()
        {
            ShowHands();
            ENT_Player p = ENT_Player.playerObject;
            _bodyReady = false;
            if (p == null) return;
            try
            {
                p.camTransform.localPosition = _camRest;
                p.noclip = false;
                p.SetGodMode(false);
                p.UnLock();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Leaving spectator mode: " + e.Message);
            }
        }

        // Every frame, after the game has moved its player.
        public void LateTick()
        {
            if (!_bodyReady) return;
            ENT_Player p = ENT_Player.playerObject;
            Rival r = Watched;
            if (p == null || r == null || !r.Ghost.HasTarget) return;

            // Out of play: no collisions (and so no triggers), nothing to grab, never dead.
            if (p.cCon != null && p.cCon.enabled) p.cCon.enabled = false;
            if (p.capsuleCollider != null && p.capsuleCollider.enabled) p.capsuleCollider.enabled = false;
            if (!p.noclip) p.noclip = true;

            Vector3 eye = r.Ghost.EyeWorld;
            Transform cam = p.camTransform;
            // The body goes where the watched player is, so the levels around them load: its camera at rest goes to
            // their eyes.
            cam.localPosition = _camRest;
            Vector3 eyeOffset = cam.position - p.transform.position;
            p.transform.position = eye - eyeOffset;

            if (_firstPerson)
            {
                p.transform.rotation = Quaternion.Euler(0f, r.Ghost.Yaw, 0f);
                cam.localRotation = Quaternion.Euler(r.Ghost.Pitch, 0f, 0f);
                if (CamY != null) CamY(p) = r.Ghost.Pitch;
                cam.position = eye;
                return;
            }

            if (!MenuUi.IsOpen && !GameBridge.GamePaused)
            {
                Vector2 look = InputManager.GetLookVector();
                _orbitYaw += look.x * OrbitSpeed;
                _orbitPitch = Mathf.Clamp(_orbitPitch - look.y * OrbitSpeed, -60f, 80f);
                float wheel = Input.mouseScrollDelta.y;
                if (wheel != 0f) _distance = Mathf.Clamp(_distance - wheel * 0.6f, MinDistance, MaxDistance);
            }
            Quaternion orbit = Quaternion.Euler(_orbitPitch, _orbitYaw, 0f);
            Vector3 back = orbit * Vector3.back;
            float distance = _distance;
            // Keep the camera on this side of walls.
            RaycastHit hit;
            if (Physics.SphereCast(eye, 0.2f, back, out hit, _distance, ~0, QueryTriggerInteraction.Ignore)) distance = Mathf.Max(0.4f, hit.distance - 0.1f);
            cam.position = eye + back * distance;
            cam.rotation = Quaternion.LookRotation(eye - cam.position, Vector3.up);
        }

        private void HideHands(ENT_Player p)
        {
            ShowHands();
            if (p.hands == null) return;
            foreach (ENT_Player.Hand hand in p.hands)
            {
                if (hand == null || hand.handModel == null) continue;
                foreach (Renderer renderer in hand.handModel.GetComponentsInChildren<Renderer>(true))
                {
                    if (!renderer.enabled) continue;
                    renderer.enabled = false;
                    _hiddenHands.Add(renderer);
                }
            }
        }

        private void ShowHands()
        {
            foreach (Renderer renderer in _hiddenHands)
                if (renderer != null) renderer.enabled = true;
            _hiddenHands.Clear();
        }

        private static AccessTools.FieldRef<ENT_Player, float> SafeRef(string name)
        {
            try { return AccessTools.FieldRefAccess<ENT_Player, float>(name); }
            catch (Exception) { return null; }
        }
    }
}
