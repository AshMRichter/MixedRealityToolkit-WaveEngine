﻿// Copyright © Wave Engine S.L. All rights reserved. Use is subject to license terms.

using System;
using System.Collections.Generic;
using System.Linq;
using WaveEngine.Common.Attributes;
using WaveEngine.Framework;
using WaveEngine.Framework.Graphics;
using WaveEngine.Framework.Physics3D;
using WaveEngine.Mathematics;
using WaveEngine.MRTK.Base.EventDatum.Input;
using WaveEngine.MRTK.Base.Interfaces.InputSystem.Handlers;

namespace WaveEngine.MRTK.SDK.Features.Input.Handlers.Manipulation
{
    /// <summary>
    /// A simple manipulation handler.
    /// </summary>
    public class SimpleManipulationHandler : Behavior, IMixedRealityPointerHandler
    {
        /// <summary>
        /// The transform.
        /// </summary>
        [BindComponent]
        protected Transform3D transform = null;

        /// <summary>
        /// The rigid body.
        /// </summary>
        [BindComponent(isRequired: false)]
        protected RigidBody3D rigidBody;

        /// <summary>
        /// The collider.
        /// </summary>
        [BindComponent(isRequired: false, isExactType: false, source: BindComponentSource.Children)]
        protected Collider3D collider = null;

        /// <summary>
        /// The physicBody3D.
        /// </summary>
        [BindComponent(isRequired: false, isExactType: false)]
        protected PhysicBody3D physicBody3D = null;

        /// <summary>
        /// Gets or sets a value indicating whether the manipulation smoothing is enabled.
        /// </summary>
        [RenderProperty(Tooltip = "Enable manipulation smoothing")]
        public bool SmoothingActive { get; set; } = true;

        /// <summary>
        /// Gets  or sets the amount of smoothing to apply to the movement, scale and rotation. 0 means no smoothing, 1 means no change to value.
        /// </summary>
        [RenderPropertyAsFInput(Tooltip = "The amount of smoothing to apply to the movement, scale and rotation. 0 means no smoothing, 1 means no change to value", MinLimit = 0, MaxLimit = 1)]
        public float SmoothingAmount { get; set; } = 0.001f;

        /// <summary>
        /// The manipulation started event.
        /// </summary>
        public event EventHandler ManipulationStarted;

        /// <summary>
        /// The glopal manipulation started event.
        /// </summary>
        public static event EventHandler AnyManipulationStarted;

        /// <summary>
        /// The manipulation ended event.
        /// </summary>
        public event EventHandler ManipulationEnded;

        /// <summary>
        /// Constraints.
        /// </summary>
        [Flags]
        public enum ConstraintsEnum : int
        {
            /// <summary>
            /// No constraints.
            /// </summary>
            None = 0,

            /// <summary>
            /// All constraints.
            /// </summary>
            All = 0xFFFF,

            /// <summary>
            /// Constraint translation on X axis.
            /// </summary>
            ConstraintPosX = 1 << 0,

            /// <summary>
            /// Constraint translation on Y axis.
            /// </summary>
            ConstraintPosY = 1 << 1,

            /// <summary>
            /// Constraint translation on Z axis.
            /// </summary>
            ConstraintPosZ = 1 << 2,

            /// <summary>
            /// Constraint position on all axes.
            /// </summary>
            ConstraintPosAll = ConstraintPosX | ConstraintPosY | ConstraintPosZ,

            /// <summary>
            /// Constraint rotation on X axis.
            /// </summary>
            ConstraintRotX = 1 << 3,

            /// <summary>
            /// Constraint rotation on Y axis.
            /// </summary>
            ConstraintRotY = 1 << 4,

            /// <summary>
            /// Constraint rotation on Z axis.
            /// </summary>
            ConstraintRotZ = 1 << 5,

            /// <summary>
            /// Constraint rotation on all axes.
            /// </summary>
            ConstraintRotAll = ConstraintRotX | ConstraintRotY | ConstraintRotZ,

            /// <summary>
            /// Constraint scale on X axis.
            /// </summary>
            ConstraintScaleX = 1 << 6,

            /// <summary>
            /// Constraint scale on Y axis.
            /// </summary>
            ConstraintScaleY = 1 << 7,

            /// <summary>
            /// Constraint scale on Z axis.
            /// </summary>
            ConstraintScaleZ = 1 << 8,

            /// <summary>
            /// Constraint scale on all axes.
            /// </summary>
            ConstraintScaleAll = ConstraintScaleX | ConstraintScaleY | ConstraintScaleZ,
        }

        /// <summary>
        /// Gets or sets constraints.
        /// </summary>
        public ConstraintsEnum Constraints { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the rigidbody should be active while dragging it.
        /// </summary>
        public bool KeepRigidBodyActiveDuringDrag { get; set; } = false;

        /// <summary>
        /// Gets or sets a value indicating whether the children colliders should be taken into account.
        /// </summary>
        public bool IncludeChildrenColliders { get; set; } = true;

        private bool lastLeftPressed;
        private bool lastRightPressed;
        private bool leftPressed;
        private bool rightPressed;

        // Transform matrix of the grabbed object in controller space at the moment the grab is started
        private Matrix4x4 grabTransform;
        private Matrix4x4 fullConstrainedRef;

        // Distance between the controllers at the moment the grab is started
        private float grabDistance;

        private readonly Dictionary<Entity, Matrix4x4> activeCursors = new Dictionary<Entity, Matrix4x4>();

        private Vector3 previousAngularFactor;
        private Vector3 previousLinearFactor;

        /// <inheritdoc/>
        protected override void OnDeactivated()
        {
            base.OnDeactivated();

            this.activeCursors.Clear();
        }

        /// <inheritdoc/>
        public void OnPointerDown(MixedRealityPointerEventData eventData)
        {
            if (eventData.EventHandled || (!this.IncludeChildrenColliders && eventData.CurrentTarget != this.Owner))
            {
                return;
            }

            var cursor = eventData.Cursor;

            if (!this.activeCursors.ContainsKey(cursor))
            {
                this.activeCursors[cursor] = this.CreateCursorTransform(eventData);

                if (this.activeCursors.Count == 1)
                {
                    if (this.rigidBody != null && !this.KeepRigidBodyActiveDuringDrag)
                    {
                        this.previousLinearFactor = this.rigidBody.LinearFactor;
                        this.previousAngularFactor = this.rigidBody.AngularFactor;

                        this.rigidBody.LinearFactor = Vector3.Zero;
                        this.rigidBody.AngularFactor = Vector3.Zero;
                    }

                    this.ManipulationStarted?.Invoke(this, EventArgs.Empty);
                    AnyManipulationStarted?.Invoke(this, EventArgs.Empty);
                }

                eventData.SetHandled();
            }
        }

        /// <inheritdoc/>
        public void OnPointerDragged(MixedRealityPointerEventData eventData)
        {
            if (eventData.EventHandled || (!this.IncludeChildrenColliders && eventData.CurrentTarget != this.Owner))
            {
                return;
            }

            var cursor = eventData.Cursor;

            if (this.activeCursors.ContainsKey(cursor))
            {
                this.activeCursors[cursor] = this.CreateCursorTransform(eventData);

                eventData.SetHandled();
            }
        }

        /// <inheritdoc/>
        public void OnPointerUp(MixedRealityPointerEventData eventData)
        {
            if (eventData.EventHandled || (!this.IncludeChildrenColliders && eventData.CurrentTarget != this.Owner))
            {
                return;
            }

            var cursor = eventData.Cursor;

            if (this.activeCursors.ContainsKey(cursor))
            {
                if (this.activeCursors.Count == 1)
                {
                    this.ReleaseRigidBody(eventData.LinearVelocity, eventData.AngularVelocity);

                    this.ManipulationEnded?.Invoke(this, EventArgs.Empty);
                }

                this.activeCursors.Remove(cursor);

                eventData.SetHandled();
            }
        }

        /// <inheritdoc/>
        public void OnPointerClicked(MixedRealityPointerEventData eventData)
        {
            // Nothing to do
        }

        private Matrix4x4 CreateCursorTransform(MixedRealityPointerEventData eventData)
        {
            return Matrix4x4.CreateFromQuaternion(eventData.Orientation) * Matrix4x4.CreateTranslation(eventData.Position);
        }

        /// <inheritdoc/>
        protected override bool OnAttached()
        {
            if (!Application.Current.IsEditor)
            {
                if (this.physicBody3D == null)
                {
                    this.physicBody3D = new StaticBody3D();
                    this.Owner.AddComponent(this.physicBody3D);
                }

                if (this.collider == null)
                {
                    this.collider = new BoxCollider3D();
                    this.Owner.AddComponent(this.collider);
                }
            }

            return base.OnAttached();
        }

        /// <inheritdoc/>
        protected override void Start()
        {
            // Ensure this is always updated after the rigidbody
            if (this.rigidBody != null)
            {
                this.UpdateOrder = this.rigidBody.UpdateOrder + 0.1f;
            }
        }

        /// <inheritdoc/>
        protected override void Update(TimeSpan gameTime)
        {
            var values = this.activeCursors.Values;
            var leftPressed = values.Count > 0;
            var rightPressed = values.Count > 1;
            var leftTransform = leftPressed ? values.ElementAt(0) : default;
            var rightTransform = rightPressed ? values.ElementAt(1) : default;

            this.DoTransform(leftPressed, rightPressed, leftTransform, rightTransform, (float)gameTime.TotalSeconds);
        }

        private void DoTransform(bool leftPressed, bool rightPressed, Matrix4x4 leftTransform, Matrix4x4 rightTransform, float timeStep)
        {
            // Update press states
            this.lastLeftPressed = this.leftPressed;
            this.lastRightPressed = this.rightPressed;
            this.leftPressed = leftPressed;
            this.rightPressed = rightPressed;

            // Apply new transform if any of the controllers is pressed
            if (this.leftPressed || this.rightPressed)
            {
                // Check whether the presses changed in this update cycle
                bool leftPressedChanged = this.leftPressed != this.lastLeftPressed;
                bool rightPressedChanged = this.rightPressed != this.lastRightPressed;

                // Current manipulation controller transform
                Matrix4x4 controllerTransform = Matrix4x4.Identity;
                var constraintsMask = (int)this.Constraints;

                if (this.leftPressed)
                {
                    if (this.rightPressed)
                    {
                        // Calculate two-hand combined world transform
                        Vector3 right = rightTransform.Translation - leftTransform.Translation;
                        Vector3 up_avg = Vector3.Lerp(leftTransform.Up, rightTransform.Up, 0.5f);

                        Vector3 position = Vector3.Lerp(leftTransform.Translation, rightTransform.Translation, 0.5f);
                        Vector3 forward = Vector3.Cross(up_avg, right);
                        Vector3 up = Vector3.Cross(right, forward);

                        controllerTransform = Matrix4x4.CreateWorld(position, forward, up);

                        // Calculate current distance between the controllers
                        var currentDistance = right.Length();

                        // Update grab distance if any of the presses changed
                        if (leftPressedChanged || rightPressedChanged)
                        {
                            this.grabDistance = currentDistance;
                        }

                        // Calculate target scale
                        var scale = Vector3.One * currentDistance / this.grabDistance;
                        if ((constraintsMask & (int)ConstraintsEnum.ConstraintScaleAll) != 0)
                        {
                            for (int i = 0; i < 3; ++i)
                            {
                                if ((constraintsMask & (1 << (i + 6))) != 0)
                                {
                                    scale[i] = 1.0f;
                                }
                            }
                        }

                        // Final controller transform
                        controllerTransform = Matrix4x4.CreateScale(scale) * controllerTransform;
                    }
                    else
                    {
                        // Set controller transform to the left transform
                        controllerTransform = leftTransform;
                    }
                }
                else if (this.rightPressed)
                {
                    // Set controller transform to the right transform
                    controllerTransform = rightTransform;
                }

                if ((constraintsMask & (int)ConstraintsEnum.ConstraintRotAll) != 0)
                {
                    Vector3 rotation = controllerTransform.Rotation;
                    for (int i = 0; i < 3; ++i)
                    {
                        if ((constraintsMask & (1 << (i + 3))) != 0)
                        {
                            rotation[i] = 0;
                        }
                    }

                    controllerTransform = Matrix4x4.CreateFromTRS(controllerTransform.Translation, rotation, controllerTransform.Scale);
                }

                // Update grab transform matrix if any of the presses changed
                if (leftPressedChanged || rightPressedChanged)
                {
                    this.fullConstrainedRef = this.transform.WorldTransform;
                    this.grabTransform = this.transform.WorldTransform * Matrix4x4.Invert(controllerTransform);
                }

                // Calculate final transformation
                var finalTransform = this.grabTransform * controllerTransform;

                if ((constraintsMask & (int)ConstraintsEnum.ConstraintPosAll) != 0)
                {
                    var translation = finalTransform.Translation;
                    for (int i = 0; i < 3; ++i)
                    {
                        if ((constraintsMask & (1 << i)) != 0)
                        {
                            translation[i] = this.fullConstrainedRef.Translation[i];
                        }
                    }

                    finalTransform.Translation = translation;
                }

                // Update object transform
                float lerpAmount = this.GetLerpAmount(timeStep);

                Vector3 pos = Vector3.Lerp(this.transform.Position, finalTransform.Translation, lerpAmount);
                Quaternion rot = Quaternion.Lerp(this.transform.Orientation, finalTransform.Orientation, lerpAmount);
                Vector3 scl = Vector3.Lerp(this.transform.Scale, finalTransform.Scale, lerpAmount);

                if (this.rigidBody != null && this.KeepRigidBodyActiveDuringDrag)
                {
                    if (this.transform.Scale != scl)
                    {
                        this.rigidBody.ResetTransform(pos, rot, scl);
                        this.transform.Scale = scl;

                        this.rigidBody.LinearVelocity = Vector3.Zero;
                        this.rigidBody.AngularVelocity = Vector3.Zero;
                    }
                    else
                    {
                        this.rigidBody.LinearVelocity = (pos - this.transform.Position) / timeStep;
                        this.rigidBody.AngularVelocity = Quaternion.ToEuler(rot * Quaternion.Inverse(this.transform.Orientation)) / timeStep;
                    }

                    this.rigidBody.WakeUp();
                }
                else
                {
                    this.rigidBody?.ResetTransform(pos, rot, scl);
                    this.transform.Position = pos;
                    this.transform.Orientation = rot;
                    this.transform.Scale = scl;
                }
            }
        }

        private float GetLerpAmount(float timeStep)
        {
            if (!this.SmoothingActive || this.SmoothingAmount == 0)
            {
                return 1;
            }

            // www.rorydriscoll.com/2016/03/07/frame-rate-independent-damping-using-lerp/
            return 1.0f - (float)Math.Pow(this.SmoothingAmount, timeStep);
        }

        private void ReleaseRigidBody(Vector3 linearVelocity, Quaternion angularVelocity)
        {
            if (this.rigidBody != null && !this.KeepRigidBodyActiveDuringDrag)
            {
                this.rigidBody.LinearFactor = this.previousLinearFactor;
                this.rigidBody.AngularFactor = this.previousAngularFactor;

                this.rigidBody.LinearVelocity = linearVelocity;
                this.rigidBody.AngularVelocity = Quaternion.ToEuler(angularVelocity);

                this.rigidBody.WakeUp();
            }
        }
    }
}
