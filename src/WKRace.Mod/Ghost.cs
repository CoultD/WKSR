using UnityEngine;
using WKRace.Protocol;

namespace WKRace.Mod
{
    // Translucent stand-in for the opponent, driven by their reported eye and hand positions.
    internal sealed class Ghost
    {
        private const float StaleSeconds = 4f;
        private const float HideBelowDistance = 0.8f;
        private const float FadeBelowDistance = 2.2f;

        private GameObject _root;
        private Transform _head;
        private Transform _body;
        private Transform _nose;
        private Transform _leftHand;
        private Transform _rightHand;
        private Material _material;

        private bool _hasTarget;
        private float _lastUpdate;
        private Vector3 _targetEye;
        private Vector3 _targetLeft;
        private Vector3 _targetRight;
        private bool _holdLeft;
        private bool _holdRight;
        private float _targetYaw;
        private Vector3 _eye;
        private Vector3 _left;
        private Vector3 _right;
        private float _yaw;
        private bool _dead;

        public bool Visible { get; private set; }
        public Vector3 EyeWorld { get { return _eye; } }

        // Positions arrive in shared coordinates, without the game's floating-origin offset.
        public void SetTarget(OpponentState s)
        {
            Vector3 offset = GameBridge.OriginOffset();
            _targetEye = new Vector3(s.X, s.Y, s.Z) + offset;
            _targetYaw = s.Yaw;
            _holdLeft = (s.Flags & StateFlags.LeftHold) != 0;
            _holdRight = (s.Flags & StateFlags.RightHold) != 0;
            if (_holdLeft) _targetLeft = new Vector3(s.LeftX, s.LeftY, s.LeftZ) + offset;
            if (_holdRight) _targetRight = new Vector3(s.RightX, s.RightY, s.RightZ) + offset;
            _dead = (s.Flags & StateFlags.Dead) != 0;
            _lastUpdate = Time.unscaledTime;

            if (!_hasTarget)
            {
                _eye = _targetEye;
                _left = _targetLeft;
                _right = _targetRight;
                _yaw = _targetYaw;
            }
            _hasTarget = true;
        }

        public void Clear()
        {
            _hasTarget = false;
            SetVisible(false);
        }

        public void Tick(bool enabled, Color color, float opacity, Camera viewer)
        {
            bool show = enabled && _hasTarget && Time.unscaledTime - _lastUpdate < StaleSeconds && !_dead;
            if (!show)
            {
                SetVisible(false);
                return;
            }
            if (_root == null && !Build()) return;

            // The floating origin can move between updates, so keep the follow target in current world space.
            float k = 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime);
            _eye = Vector3.Lerp(_eye, _targetEye, k);
            _yaw = Mathf.LerpAngle(_yaw, _targetYaw, k);
            if (_holdLeft) _left = Vector3.Lerp(_left, _targetLeft, k);
            if (_holdRight) _right = Vector3.Lerp(_right, _targetRight, k);

            float alpha = opacity;
            if (viewer != null)
            {
                float distance = Vector3.Distance(viewer.transform.position, _eye);
                if (distance < HideBelowDistance)
                {
                    SetVisible(false);
                    return;
                }
                alpha *= Mathf.Clamp01((distance - HideBelowDistance) / (FadeBelowDistance - HideBelowDistance));
            }

            Quaternion facing = Quaternion.Euler(0f, _yaw, 0f);
            _root.transform.SetPositionAndRotation(_eye, facing);
            _head.localPosition = Vector3.zero;
            _body.localPosition = new Vector3(0f, -0.62f, 0f);
            _nose.localPosition = new Vector3(0f, -0.02f, 0.2f);

            Vector3 down = new Vector3(0f, -0.62f, 0f);
            _leftHand.position = _holdLeft ? _left : _eye + facing * (new Vector3(-0.38f, 0f, 0.1f) + down);
            _rightHand.position = _holdRight ? _right : _eye + facing * (new Vector3(0.38f, 0f, 0.1f) + down);

            Color c = color;
            c.a = alpha;
            _material.color = c;
            SetVisible(true);
        }

        public void Destroy()
        {
            if (_root != null) Object.Destroy(_root);
            _root = null;
        }

        private void SetVisible(bool visible)
        {
            Visible = visible;
            if (_root != null && _root.activeSelf != visible) _root.SetActive(visible);
        }

        private bool Build()
        {
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                Plugin.Log.LogWarning("No usable shader for the ghost; it will not be drawn.");
                return false;
            }
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

            _root = new GameObject("WKRace Ghost") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(_root);
            _head = Part(PrimitiveType.Sphere, new Vector3(0.34f, 0.34f, 0.34f));
            _body = Part(PrimitiveType.Capsule, new Vector3(0.46f, 0.42f, 0.46f));
            _nose = Part(PrimitiveType.Cube, new Vector3(0.09f, 0.09f, 0.18f));
            _leftHand = Part(PrimitiveType.Sphere, new Vector3(0.18f, 0.18f, 0.18f));
            _rightHand = Part(PrimitiveType.Sphere, new Vector3(0.18f, 0.18f, 0.18f));
            return true;
        }

        private Transform Part(PrimitiveType type, Vector3 scale)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.hideFlags = HideFlags.HideAndDontSave;
            Collider collider = go.GetComponent<Collider>();
            if (collider != null) Object.Destroy(collider);

            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            go.transform.SetParent(_root.transform, false);
            go.transform.localScale = scale;
            return go.transform;
        }
    }
}
