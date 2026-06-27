using System;
using Shared.OxySync.Attributes;
using UnityEngine;

namespace Shared.OxySync
{
    public abstract class NetworkTransform : NetworkBehaviour
    {
        [SyncVar(Epsilon = 0.01f)]
        protected Vector3 _netPosition;

        [SyncVar(Epsilon = 0.01f)]
        protected Quaternion _netRotation;

        [SyncVar(Epsilon = 0.01f)]
        protected Vector3 _netScale;

        public bool syncPosition = true;
        public bool syncRotation;
        public bool syncScale;

        public bool interpolatePosition = true;
        public bool interpolateRotation = true;
        public bool interpolateScale = true;

        public enum CoordinateSpace { Local, World }
        public enum UpdateMethod { Update, FixedUpdate, LateUpdate }

        public Transform target;

        public CoordinateSpace coordinateSpace = CoordinateSpace.World;
        public UpdateMethod updateMethod = UpdateMethod.Update;

        public float snapThreshold = 1.5f;
        public float lerpSpeed = 15f;

        protected float _lastRequestTime;
        protected const float REQUEST_COOLDOWN = 0.5f;

        public override void OnSpawn()
        {
            base.OnSpawn();
            if (target == null) target = transform;
            SyncInterval = 0.05f;
        }

        private void Update()
        {
            if (updateMethod == UpdateMethod.Update)
                Poll();
        }

        private void FixedUpdate()
        {
            if (updateMethod == UpdateMethod.FixedUpdate)
                Poll();
        }

        private void LateUpdate()
        {
            if (updateMethod == UpdateMethod.LateUpdate)
                Poll();
        }

        private void Poll()
        {
            if (NetId == 0 || !inSession) return;

            if (isServer)
                ServerUpdate();
            else
                ClientUpdate();
        }

        [Server]
        protected virtual void ServerUpdate()
        {
            if (syncPosition)
            {
                _netPosition = coordinateSpace == CoordinateSpace.Local
                    ? target.localPosition
                    : target.position;
            }
            if (syncRotation)
            {
                _netRotation = coordinateSpace == CoordinateSpace.Local
                    ? target.localRotation
                    : target.rotation;
            }
            if (syncScale)
            {
                _netScale = coordinateSpace == CoordinateSpace.Local
                    ? target.localScale
                    : target.lossyScale;
            }
        }

        [Client]
        protected virtual void ClientUpdate()
        {
            if (syncPosition)
            {
                Vector3 currentPos = coordinateSpace == CoordinateSpace.Local
                    ? target.localPosition
                    : target.position;

                float dist = Vector3.Distance(currentPos, _netPosition);

                if (dist > snapThreshold)
                {
                    if (coordinateSpace == CoordinateSpace.Local)
                        target.localPosition = _netPosition;
                    else
                        target.position = _netPosition;
                }
                else if (interpolatePosition)
                {
                    Vector3 lerped = Vector3.Lerp(currentPos, _netPosition, Mathf.Clamp01(lerpSpeed * Time.unscaledDeltaTime));
                    if (coordinateSpace == CoordinateSpace.Local)
                        target.localPosition = lerped;
                    else
                        target.position = lerped;
                }
                else if (coordinateSpace == CoordinateSpace.Local)
                {
                    target.localPosition = _netPosition;
                }
                else
                {
                    target.position = _netPosition;
                }
            }

            if (syncRotation)
            {
                if (interpolateRotation)
                {
                    Quaternion currentRot = coordinateSpace == CoordinateSpace.Local
                        ? target.localRotation
                        : target.rotation;
                    Quaternion slerped = Quaternion.Slerp(currentRot, _netRotation, Mathf.Clamp01(lerpSpeed * Time.unscaledDeltaTime));
                    if (coordinateSpace == CoordinateSpace.Local)
                        target.localRotation = slerped;
                    else
                        target.rotation = slerped;
                }
                else if (coordinateSpace == CoordinateSpace.Local)
                {
                    target.localRotation = _netRotation;
                }
                else
                {
                    target.rotation = _netRotation;
                }
            }

            if (syncScale)
            {
                if (interpolateScale)
                {
                    Vector3 currentScale = coordinateSpace == CoordinateSpace.Local
                        ? target.localScale
                        : target.lossyScale;
                    Vector3 lerped = Vector3.Lerp(currentScale, _netScale, Mathf.Clamp01(lerpSpeed * Time.unscaledDeltaTime));
                    if (coordinateSpace == CoordinateSpace.Local)
                        target.localScale = lerped;
                }
                else if (coordinateSpace == CoordinateSpace.Local)
                {
                    target.localScale = _netScale;
                }
            }

            TryRequestPosition();
        }

        [Client]
        private void TryRequestPosition()
        {
            if (!ShouldRequestPosition()) return;

            if (Time.unscaledTime - _lastRequestTime < REQUEST_COOLDOWN)
                return;

            _lastRequestTime = Time.unscaledTime;
            CallCommand(nameof(CmdRequestPositionSync), LocalUserIdQuery?.Invoke() ?? 0);
        }

        protected virtual bool ShouldRequestPosition()
        {
            return false;
        }

        [Command]
        protected void CmdRequestPositionSync(ulong requesterId)
        {
            OnServerPositionRequest(requesterId);
        }

        protected virtual void OnServerPositionRequest(ulong requesterId)
        {
            CallTargetRpc(requesterId, nameof(RpcReceivePosition),
                target.position, target.rotation, target.localScale);
        }

        [TargetRpc]
        protected void RpcReceivePosition(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            _netPosition = position;
            _netRotation = rotation;
            _netScale = scale;
            OnPositionReceived(position, rotation, scale);
        }

        protected virtual void OnPositionReceived(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            if (syncPosition)
            {
                if (coordinateSpace == CoordinateSpace.Local)
                    target.localPosition = position;
                else
                    target.position = position;
            }
            if (syncRotation)
            {
                if (coordinateSpace == CoordinateSpace.Local)
                    target.localRotation = rotation;
                else
                    target.rotation = rotation;
            }
            if (syncScale)
            {
                if (coordinateSpace == CoordinateSpace.Local)
                    target.localScale = scale;
            }
            _lastRequestTime = Time.unscaledTime;
        }
    }
}
