﻿// Copyright © Wave Engine S.L. All rights reserved. Use is subject to license terms.

using System;
using System.Collections.Generic;
using System.Linq;
using WaveEngine.Framework;
using WaveEngine.Framework.Graphics;
using WaveEngine.Framework.Managers;
using WaveEngine.Framework.Physics3D;
using WaveEngine.Mathematics;
using WaveEngine.MRTK.Base.EventDatum.Input;
using WaveEngine.MRTK.Base.Interfaces.InputSystem.Handlers;

namespace WaveEngine.MRTK.Emulation
{
    /// <summary>
    /// The Cursor Manager.
    /// </summary>
    public class CursorManager : UpdatableSceneManager
    {
        private static readonly int VELOCITY_HISTORY_SIZE = 10;

        /// <summary>
        /// Gets the cursors.
        /// </summary>
        public List<Cursor> Cursors { get; private set; } = new List<Cursor>();

        private Dictionary<Entity, LinkedList<Entity>> cursorCollisions = new Dictionary<Entity, LinkedList<Entity>>();
        private Dictionary<Entity, Entity> interactedEntities = new Dictionary<Entity, Entity>();

        private Dictionary<Entity, Vector3> cursorsLinearVelocity = new Dictionary<Entity, Vector3>();
        private Dictionary<Entity, Quaternion> cursorsAngularVelocity = new Dictionary<Entity, Quaternion>();

        private Dictionary<Cursor, LinkedList<Vector3>> cursorsPositionHistory = new Dictionary<Cursor, LinkedList<Vector3>>();
        private Dictionary<Cursor, LinkedList<Quaternion>> cursorsOrientationHistory = new Dictionary<Cursor, LinkedList<Quaternion>>();
        private LinkedList<float> gameTimeHistory = new LinkedList<float>();

        /// <summary>
        /// Adds a cursor.
        /// </summary>
        /// <param name="cursor">The cursor.</param>
        public void AddCursor(Cursor cursor)
        {
            this.Cursors.Add(cursor);

            cursor.StaticBody3D.BeginCollision += this.Cursor_BeginCollision;
            cursor.StaticBody3D.UpdateCollision += this.Cursor_UpdateCollision;
            cursor.StaticBody3D.EndCollision += this.Cursor_EndCollision;

            this.cursorsPositionHistory[cursor] = new LinkedList<Vector3>();
            this.cursorsOrientationHistory[cursor] = new LinkedList<Quaternion>();
        }

        /// <summary>
        /// Removes a cursor.
        /// </summary>
        /// <param name="cursor">The cursor.</param>
        public void RemoveCursor(Cursor cursor)
        {
            this.Cursors.Remove(cursor);

            cursor.StaticBody3D.BeginCollision -= this.Cursor_BeginCollision;
            cursor.StaticBody3D.UpdateCollision -= this.Cursor_UpdateCollision;
            cursor.StaticBody3D.EndCollision -= this.Cursor_EndCollision;

            this.cursorsPositionHistory.Remove(cursor);
            this.cursorsOrientationHistory.Remove(cursor);
        }

        /// <inheritdoc/>
        protected override void OnDeactivated()
        {
            base.OnDeactivated();

            foreach (var cursor in this.Cursors)
            {
                cursor.StaticBody3D.BeginCollision -= this.Cursor_BeginCollision;
                cursor.StaticBody3D.UpdateCollision -= this.Cursor_UpdateCollision;
                cursor.StaticBody3D.EndCollision -= this.Cursor_EndCollision;
            }
        }

        private void Cursor_BeginCollision(object sender, CollisionInfo3D info)
        {
            var cursorEntity = info.ThisBody.Owner;
            var interactedEntity = info.OtherBody.Owner;

            this.RunTouchHandlers(cursorEntity, interactedEntity, (h, e) => h?.OnTouchStarted(e));

            if (!this.cursorCollisions.ContainsKey(cursorEntity))
            {
                this.cursorCollisions[cursorEntity] = new LinkedList<Entity>();
            }

            this.cursorCollisions[cursorEntity].AddFirst(interactedEntity);
        }

        private void Cursor_UpdateCollision(object sender, CollisionInfo3D info)
        {
            var cursorEntity = info.ThisBody.Owner;
            var interactedEntity = info.OtherBody.Owner;

            this.RunTouchHandlers(cursorEntity, interactedEntity, (h, e) => h?.OnTouchUpdated(e));
        }

        private void Cursor_EndCollision(object sender, CollisionInfo3D info)
        {
            var cursorEntity = info.ThisBody.Owner;
            var interactedEntity = info.OtherBody.Owner;

            this.RunTouchHandlers(cursorEntity, interactedEntity, (h, e) => h?.OnTouchCompleted(e));

            if (this.cursorCollisions.ContainsKey(cursorEntity))
            {
                this.cursorCollisions[cursorEntity].Remove(interactedEntity);
            }
        }

        /// <inheritdoc/>
        public override void Update(TimeSpan gameTime)
        {
            // Update gameTime history
            this.AddToHistoryList(this.gameTimeHistory, (float)gameTime.TotalSeconds);

            // Compute history elapsed time
            float historyElapsedTime = this.gameTimeHistory.Sum();

            // Update cursors velocities
            foreach (var cursor in this.Cursors)
            {
                var positionHistory = this.cursorsPositionHistory[cursor];
                var orientationHistory = this.cursorsOrientationHistory[cursor];

                this.AddToHistoryList(positionHistory, cursor.transform.Position);
                this.AddToHistoryList(orientationHistory, cursor.transform.Orientation);

                var linearVelocity = (positionHistory.Last.Value - positionHistory.First.Value) / historyElapsedTime;
                var angularVelocity = (orientationHistory.Last.Value * Quaternion.Inverse(orientationHistory.First.Value)) * (1 / historyElapsedTime);

                this.cursorsLinearVelocity[cursor.Owner] = linearVelocity;
                this.cursorsAngularVelocity[cursor.Owner] = angularVelocity;
            }

            foreach (var entry in this.cursorCollisions)
            {
                if (entry.Value.Count == 0)
                {
                    continue;
                }

                var cursorEntity = entry.Key;
                var interactedEntity = entry.Value.First.Value;

                var cursorComponent = cursorEntity.FindComponent<Cursor>();

                if (!cursorComponent.PreviousPinch && cursorComponent.Pinch && cursorComponent.IsActivated)
                {
                    // PointerDown when the cursor transitions to pinched while inside a collider
                    this.RunPointerHandlers(cursorEntity, interactedEntity, (h, e) => h?.OnPointerDown(e));
                    this.interactedEntities[cursorEntity] = interactedEntity;
                }
            }

            var finishedInteractions = new List<Entity>();
            foreach (var entry in this.interactedEntities)
            {
                var cursorEntity = entry.Key;
                var interactedEntity = entry.Value;

                var cursorComponent = cursorEntity.FindComponent<Cursor>();

                if (cursorComponent.PreviousPinch)
                {
                    if (cursorComponent.Pinch)
                    {
                        // PointerDragged while the cursor is pinched
                        this.RunPointerHandlers(cursorEntity, interactedEntity, (h, e) => h?.OnPointerDragged(e));
                    }
                    else
                    {
                        // PointerUp when the cursor is unpinched
                        this.RunPointerHandlers(cursorEntity, interactedEntity, (h, e) => h?.OnPointerUp(e));

                        finishedInteractions.Add(cursorEntity);
                    }
                }
            }

            // Remove finished interactions
            foreach (var cursor in finishedInteractions)
            {
                this.interactedEntities.Remove(cursor);
            }
        }

        private void RunTouchHandlers(Entity cursor, Entity other, Action<IMixedRealityTouchHandler, HandTrackingInputEventData> action)
        {
            var position = cursor.FindComponent<Transform3D>().Position;

            var eventArgs = new HandTrackingInputEventData()
            {
                Cursor = cursor,
                Position = position,
            };

            var interactables = this.GatherComponents(other)
                                    .Where(x => x.IsActivated)
                                    .OfType<IMixedRealityTouchHandler>();

            foreach (var touchHandler in interactables)
            {
                action(touchHandler, eventArgs);
            }
        }

        private void RunPointerHandlers(Entity cursor, Entity other, Action<IMixedRealityPointerHandler, MixedRealityPointerEventData> action)
        {
            var transform = cursor.FindComponent<Transform3D>();

            var eventArgs = new MixedRealityPointerEventData()
            {
                Cursor = cursor,
                CurrentTarget = other,
                Position = transform.Position,
                Orientation = transform.Orientation,
                LinearVelocity = this.cursorsLinearVelocity[cursor],
                AngularVelocity = this.cursorsAngularVelocity[cursor],
            };

            var interactables = this.GatherComponents(other)
                                    .Where(x => x.IsActivated)
                                    .OfType<IMixedRealityPointerHandler>();

            foreach (var touchHandler in interactables)
            {
                action(touchHandler, eventArgs);
            }
        }

        private IEnumerable<Component> GatherComponents(Entity entity)
        {
            List<Component> result = new List<Component>();

            Entity current = entity;

            while (current != null)
            {
                result.AddRange(current.Components);
                current = current.Parent;
            }

            return result;
        }

        private void AddToHistoryList<T>(LinkedList<T> list, T newItem)
        {
            list.AddLast(newItem);

            if (list.Count > VELOCITY_HISTORY_SIZE)
            {
                list.RemoveFirst();
            }
        }
    }
}
