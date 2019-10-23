﻿using Newtonsoft.Json;
using RTSLockstep.Pathfinding;
using RTSLockstep.Grid;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace RTSLockstep
{
    public class Move : ActiveAbility
    {
        //Stop multipliers determine accuracy required for stopping on the destination
        public const long GroupStop = FixedMath.One / 4;
        public const long GroupDirectStop = FixedMath.One;
        public const long DirectStop = FixedMath.One / 4;

        public long StopMultiplier { get; set; }

        //Has this unit arrived at destination? Default set to false.
        public bool Arrived { get; private set; }

        //Called when unit arrives at destination
        public event Action onArrive;
        public event Action onStartMove;
        //Called whenever movement is stopped... i.e. to attack
        public event Action OnStopMove;

        [Lockstep(true)]
        public bool SlowArrival { get; set; }
        public Vector2d AveragePosition { get; set; }

        // add a little padding to manevour around structures
        public int GridSize { get { return (int)Math.Round(cachedBody.Radius.CeilToInt() * 1.5f, MidpointRounding.AwayFromZero); } }

        public Vector2d Position { get { return cachedBody.Position; } }

        public long CollisionSize { get { return cachedBody.Radius; } }

        public MovementGroup MyMovementGroup { get; set; }
        public int MyMovementGroupID { get; set; }

        public bool IsGroupMoving { get; set; }

        public bool IsMoving { get; private set; }

        [HideInInspector]
        public Vector2d Destination;
        [HideInInspector]
        public bool IsAvoidingLeft;
        private long _minAvoidanceDistance;

        private const int MinimumOtherStopTime = LockstepManager.FrameRate / 4;
        private const int StuckTimeThreshold = LockstepManager.FrameRate / 4;
        private const int StuckRepathTries = 4;

        private bool hasPath;
        private bool straightPath;

        // key = world position, value = vector flow field
        private Dictionary<Vector2d, FlowField> _flowFields = new Dictionary<Vector2d, FlowField>();
        private Dictionary<Vector2d, FlowField> _flowFieldBuffer;

        private int StoppedTime;

        #region Auto stopping properites
        private const int AUTO_STOP_PAUSE_TIME = LockstepManager.FrameRate / 8;
        private int AutoStopPauser;

        private int StopPauseLayer;

        private int CollisionStopPauser;

        private int StopPauseLooker;
        #endregion

        private LSBody cachedBody { get; set; }
        private Turn cachedTurn { get; set; }
        private Attack cachedAttack { get; set; }

        private long timescaledAcceleration;
        private long timescaledDecceleration;
        private bool decelerating;

        public GridNode currentNode;
        private GridNode destinationNode;

        private bool _allowUnwalkableEndNode;

        private Vector2d movementDirection;
        private Vector2d lastDirection;

        // How far we move each update
        private long distanceToMove;
        // How far away the agent stops from the target
        private long closingDistance;
        private long stuckTolerance;

        private int stuckTime;

        private int RepathTries;
        private bool DoPathfind;

        private readonly int collidedCount;
        private readonly ushort collidedID;

        private RTSAgent tempAgent;
        private readonly bool paused;
        private Vector2d desiredVelocity;

        [HideInInspector]
        public bool CanMove = true;
        private bool canTurn;

        #region Serialized
        [SerializeField, FixedNumber]
        private long _speed = FixedMath.One * 4;
        public virtual long Speed { get { return _speed; } }
        [SerializeField, FixedNumber]
        private long _acceleration = FixedMath.One * 4;
        public long Acceleration { get { return _acceleration; } }
        [SerializeField, Tooltip("Disable if unit doesn't need to find path, i.e. flying")]
        private bool _canPathfind = true;
        public bool CanPathfind { get { return _canPathfind; } set { _canPathfind = value; } }
        public bool DrawPath;

        public event Action onGroupProcessed;
        public bool MoveOnGroupProcessed { get; private set; }
        #endregion

        protected override void OnSetup()
        {
            cachedBody = Agent.Body;
            cachedBody.onContact += HandleCollision;
            cachedAttack = Agent.GetAbility<Attack>();
            cachedTurn = Agent.GetAbility<Turn>();
            canTurn = cachedTurn.IsNotNull();

            DrawPath = false;

            timescaledAcceleration = Acceleration.Mul(Speed) / LockstepManager.FrameRate;
            //Cleaner stops with more decelleration
            timescaledDecceleration = timescaledAcceleration * 4;
            //Fatter objects can afford to land imprecisely
            closingDistance = cachedBody.Radius;
            stuckTolerance = ((cachedBody.Radius * Speed) >> FixedMath.SHIFT_AMOUNT) / LockstepManager.FrameRate;
            stuckTolerance *= stuckTolerance;
            SlowArrival = true;
        }

        protected override void OnInitialize()
        {
            StoppedTime = 0;

            IsGroupMoving = false;
            MyMovementGroupID = -1;

            AutoStopPauser = 0;
            CollisionStopPauser = 0;
            StopPauseLooker = 0;
            StopPauseLayer = 0;

            StopMultiplier = DirectStop;

            Destination = Vector2d.zero;
            hasPath = false;
            IsMoving = false;
            IsAvoidingLeft = false;
            stuckTime = 0;
            RepathTries = 0;

            Arrived = true;
            AveragePosition = Agent.Body.Position;
            DoPathfind = false;
        }

        protected override void OnSimulate()
        {
            if (CanMove)
            {
                //TODO: Organize/split this function
                if (IsMoving)
                {
                    // check if agent has to pathfind, otherwise straight path to rely on destination
                    if (CanPathfind)
                    {
                        GetMovementPath();
                    }

                    // we only need to set velocity if we're going somewhere
                    if (hasPath || straightPath)
                    {
                        SetMovementVelocity();
                    }
                    else
                    {
                        //agent shouldn't be moving then and is stuck...
                        StopMove();
                    }
                }
                // agent is not moving
                else
                {
                    decelerating = true;

                    //Slowin' down
                    if (cachedBody.VelocityMagnitude > 0)
                    {
                        cachedBody.Velocity += GetAdjustVector(Vector2d.zero);
                    }

                    StoppedTime++;
                }
                decelerating = false;

                AutoStopPauser--;
                CollisionStopPauser--;
                StopPauseLooker--;
                AveragePosition = AveragePosition.Lerped(Agent.Body.Position, FixedMath.One / 2);
            }
        }

        private void GetMovementPath()
        {
            if (Pathfinder.GetStartNode(Position, out currentNode)
                || Pathfinder.GetClosestViableNode(Position, Position, this.GridSize, out currentNode))
            {
                if (DoPathfind)
                {
                    DoPathfind = false;

                    // if size requires consideration, use old next-best-node system
                    // also a catch in case GetEndNode returns null
                    if (GridSize <= 1 && Pathfinder.GetEndNode(Position, Destination, out destinationNode)
                        || Pathfinder.GetClosestViableNode(Position, Destination, GridSize, out destinationNode))
                    {
                        Destination = destinationNode.WorldPos;
                        if (currentNode.DoesEqual(destinationNode))
                        {
                            if (RepathTries >= 1)
                            {
                                Arrive();
                            }
                        }
                        else
                        {
                            CheckPath();
                        }
                    }
                    else
                    {
                        hasPath = false;
                    }
                }
            }
        }

        private void CheckPath()
        {
            if (Pathfinder.NeedsPath(currentNode, destinationNode, GridSize))
            {
                if (straightPath)
                {
                    straightPath = false;
                }

                PathRequestManager.RequestPath(currentNode, destinationNode, GridSize, (flowFields, success) =>
                {
                    if (success)
                    {
                        hasPath = true;
                        _flowFields.Clear();
                        _flowFields = flowFields;
                    }
                });
            }
            else
            {
                straightPath = true;
            }
        }

        private void SetMovementVelocity()
        {
            movementDirection = Vector2d.zero;
            desiredVelocity = Vector2d.zero;

            if (straightPath)
            {
                // no need to check flow field, we got LOS
                movementDirection = Destination - cachedBody.Position;
            }
            else
            {
                // Calculate steering and flocking forces for all agents
                // work out the force to apply to us based on the flow field grid squares we are on.
                // http://en.wikipedia.org/wiki/Bilinear_interpolation#Nonlinear

                _flowFieldBuffer = !IsGroupMoving ? _flowFields : MyMovementGroup.GroupFlowFields;

                if (_flowFieldBuffer.TryGetValue(currentNode.GridPos, out FlowField flowField))
                {
                    if (flowField.HasLOS)
                    {
                        // we have no more use for flow fields if the agent has line of sight to destination
                        straightPath = true;
                        movementDirection = Destination - cachedBody.Position;
                    }
                    else
                    {
                        movementDirection = SteeringBehaviorFlowField();
                    }

                    lastDirection = movementDirection;
                }
                else
                {
                    //vector not found
                    //If we are centered on a grid square with no flow vector this will happen
                    if (movementDirection.Equals(Vector2d.zero))
                    {
                        //we need to keep moving on...
                        movementDirection = lastDirection.IsNotNull() ? lastDirection :  Destination - cachedBody.Position;
                    }
                }
            }

            // This is now the direction we want to be travelling in 
            // needs to be normalized
            movementDirection.Normalize(out distanceToMove);

            if (IsGroupMoving)
            {
                movementDirection += CalculateGroupBehaviors();
            }

            // avoid any intersection agents!
            movementDirection += SteeringBehaviourAvoid();

            long stuckThreshold = timescaledAcceleration / LockstepManager.FrameRate;
            long slowDistance = cachedBody.VelocityMagnitude.Div(timescaledDecceleration);

            if (distanceToMove > slowDistance)
            {
                desiredVelocity = movementDirection;
                if (canTurn)
                {
                    cachedTurn.StartTurnDirection(movementDirection);
                }
            }
            else
            {
                if (distanceToMove < FixedMath.Mul(closingDistance, StopMultiplier))
                {
                    Arrive();
                    //TODO: Don't skip this frame of slowing down
                    return;
                }

                if (distanceToMove > closingDistance)
                {
                    if (canTurn)
                    {
                        cachedTurn.StartTurnDirection(movementDirection);
                    }
                }

                if (distanceToMove <= slowDistance)
                {
                    long closingSpeed = distanceToMove.Div(slowDistance);
                    if (canTurn)
                    {
                        cachedTurn.StartTurnDirection(movementDirection);
                    }

                    desiredVelocity = movementDirection * closingSpeed;
                    decelerating = true;
                    //Reduce occurence of units preventing other units from reaching destination
                    stuckThreshold *= 5;
                }
            }

            //If unit has not moved stuckThreshold in a frame, it's stuck
            stuckTime++;
            if (GetCanAutoStop())
            {
                if (Agent.Body.Position.FastDistance(AveragePosition) <= (stuckThreshold * stuckThreshold))
                {
                    if (stuckTime > StuckTimeThreshold)
                    {
                        if (RepathTries < StuckRepathTries)
                        {
                            Debug.Log("Stuck Agent");
                            if (!IsGroupMoving)
                            {
                                // attempt to repath if agent is by themselves
                                // otherwise we already have the best path based on the group
                                DoPathfind = true;
                            }

                            RepathTries++;
                        }
                        else
                        {
                            Debug.Log("Stuck Agent Stopping");
                            // we've tried to many times, we stuck stuck
                            Arrive();
                        }
                        stuckTime = 0;
                    }
                }
                else
                {
                    if (stuckTime > 0)
                    {
                        stuckTime -= 1;
                    }

                    RepathTries = 0;
                }
            }

            // cap accelateration
            long currentVelocity = desiredVelocity.SqrMagnitude();
            if (currentVelocity > Acceleration)
            {
                desiredVelocity *= (Acceleration / FixedMath.Sqrt(currentVelocity)).CeilToInt();
            }

            //Multiply our direction by speed for our desired speed
            desiredVelocity *= Speed;

            // Cap speed as required
            var currentSpeed = cachedBody.Velocity.Magnitude();
            if (currentSpeed > Speed)
            {
                desiredVelocity *= (Speed / currentSpeed).CeilToInt();
            }

            //Apply the force
            cachedBody.Velocity += GetAdjustVector(desiredVelocity);
        }

        private Vector2d SteeringBehaviorFlowField()
        {

            //Work out the force to apply to us based on the flow field grid squares we are on.
            //we apply bilinear interpolation on the 4 grid squares nearest to us to work out our force.
            // http://en.wikipedia.org/wiki/Bilinear_interpolation#Nonlinear

            //Top left Coordinate of the 4
            Vector2d gridPos = currentNode.GridPos;
            int floorX = gridPos.x.CeilToInt();
            int floorY = gridPos.y.CeilToInt();

            //The 4 weights we'll interpolate, see http://en.wikipedia.org/wiki/File:Bilininterp.png for the coordinates

            Vector2d f00 = _flowFieldBuffer.ContainsKey(new Vector2d(floorX, floorY)) ? _flowFieldBuffer[new Vector2d(floorX, floorY)].Direction : Vector2d.zero;
            Vector2d f01 = _flowFieldBuffer.ContainsKey(new Vector2d(floorX, floorY + 1)) ? _flowFieldBuffer[new Vector2d(floorX, floorY)].Direction : Vector2d.zero;
            Vector2d f10 = _flowFieldBuffer.ContainsKey(new Vector2d(floorX + 1, floorY)) ? _flowFieldBuffer[new Vector2d(floorX, floorY)].Direction : Vector2d.zero;
            Vector2d f11 = _flowFieldBuffer.ContainsKey(new Vector2d(floorX + 1, floorY + 1)) ? _flowFieldBuffer[new Vector2d(floorX, floorY)].Direction : Vector2d.zero;

            //Do the x interpolations
            int xWeight = gridPos.x.ToInt() - floorX;

            Vector2d top = f00 * (1 - xWeight) + (f10 * xWeight);
            Vector2d bottom = f01 * (1 - xWeight) + (f11 * xWeight);

            //Do the y interpolation
            int yWeight = gridPos.y.ToInt() - floorY;

            //This is now the direction we want to be travelling in (needs to be normalized)
            Vector2d desiredDirection = top * (1 - yWeight) + (bottom * yWeight);

            //If we are centered on a grid square with no vector this will happen
            if (desiredDirection.Equals(Vector2d.zero))
            {
                return Vector2d.zero;
            }

            return desiredDirection;
        }

        //  Calculate steering and flocking forces for all agents
        private Vector2d CalculateGroupBehaviors()
        {
            int _neighboursCount = 0;
            long neighborRadius = FixedMath.One * 3;

            Vector2d totalForce = Vector2d.zero;
            Vector2d averageHeading = Vector2d.zero;
            Vector2d centerOfMass = Vector2d.zero;

            for (int i = 0; i < AgentController.GlobalAgents.Length; i++)
            {
                bool neighborFound = false;
                RTSAgent a = AgentController.GlobalAgents[i];
                if (a.IsNotNull() && a != Agent)
                {
                    Vector2d distance = Position - a.Body.Position;
                    //  Normalize returns the magnitude to use for calculations
                    distance.Normalize(out long distanceMag);

                    // agent is within range of neighbor
                    if (distanceMag < neighborRadius)
                    {
                        //  Move away from agents we are too close too
                        //  Vector away from other agent
                        totalForce += (distance * (1 - (distanceMag / neighborRadius)));

                        //  Change our direction to be closer to our neighbours
                        //  That are within the max distance and are moving
                        if (a.Body.VelocityMagnitude > 0)
                        {
                            //Sum up our headings
                            Vector2d head = a.Body._velocity;
                            head.Normalize();
                            averageHeading += head;
                        }

                        //  Move nearer to those entities we are near but not near enough to
                        //  Sum up the position of our neighbours
                        centerOfMass += a.Body.Position;

                        neighborFound = true;
                    }

                    if (neighborFound)
                    {
                        _neighboursCount++;
                    }
                }
            }

            if (_neighboursCount > 0)
            {
                //  Separation calculates a force to move away from all of our neighbours. 
                //  We do this by calculating a force from them to us and scaling it so the force is greater the nearer they are.
                Vector2d _seperation = totalForce * (Acceleration / _neighboursCount);

                //  Cohesion and Alignment are only for when other agents going to a similar location as us, 
                //  otherwise we’ll get caught up when other agents move past.

                //  Alignment calculates a force so that our direction is closer to our neighbours.
                //  It does this similar to cohesion, but by summing up the direction vectors (normalised velocities) of ourself 
                //  and our neighbours and working out the average direction.
                //  Divide by amount of neighbors to get the average heading
                Vector2d _alignment = (averageHeading / _neighboursCount);
                //  Cohesion calculates a force that will bring us closer to our neighbours, so we move together as a group rather than individually.
                //  Cohesion calculates the average position of our neighbours and ourself, and steers us towards it
                //  seek this position
                Vector2d _cohesion = SteeringBehaviorSeek(centerOfMass / _neighboursCount);

                //Combine them to come up with a total force to apply, decreasing the effect of cohesion
                return (_seperation * 2) + (_alignment * FixedMath.Create(0.5f)) + (_cohesion * FixedMath.Create(0.2f));
            }
            else
            {
                return Vector2d.zero;
            }
        }

        private Vector2d SteeringBehaviorSeek(Vector2d _destination)
        {
            if (_destination == Position)
            {
                return Vector2d.zero;
            }

            //Desired change of location
            Vector2d desired = _destination - Position;
            desired.Normalize(out long desiredMag);
            //Desired velocity (move there at maximum speed)
            return desiredMag > 0 ? desired * (Speed / desiredMag) : Vector2d.zero;
        }

        protected virtual Func<RTSAgent, bool> AvoidAgentConditional
        {
            get
            {
                Func<RTSAgent, bool> agentConditional = (other) =>
                {
                    // check to make sure we didn't find ourselves and that the other agent can move
                    if (Agent.GlobalID != other.GlobalID
                    && other.GetAbility<Move>()
                    && other.GetAbility<Move>().IsMoving)
                    {
                        Vector2d distance = (other.Body.Position - Position);
                        distance.Normalize(out long distanceMag);

                        if (distanceMag < _minAvoidanceDistance)
                        {
                            _minAvoidanceDistance = distanceMag;
                            return true;
                        }
                    }

                    // we don't need to avoid!
                    return false;
                };

                return agentConditional;
            }
        }

        private Vector2d SteeringBehaviourAvoid()
        {
            if (Agent.Body.Velocity.SqrMagnitude() <= Agent.Body.Radius)
            {
                return Vector2d.zero;
            }

            _minAvoidanceDistance = FixedMath.One * 6;

            Func<RTSAgent, bool> avoidAgentConditional = AvoidAgentConditional;

            RTSAgent closetAgent = InfluenceManager.Scan(
                     Position,
                     cachedAttack.Sight,
                     avoidAgentConditional,
                     (bite) =>
                     {
                         // trying to avoid all agents, we don't care about alliances!
                         return true;
                     }
                 );

            if (closetAgent.IsNull())
            {
                return Vector2d.zero;
            }

            Vector2d resultVector = Vector2d.zero;

            LSBody collisionBody = closetAgent.Body;
            long ourVelocityLengthSquared = Agent.Body.Velocity.SqrMagnitude();
            Vector2d combinedVelocity = Agent.Body.Velocity + collisionBody.Velocity;
            long combinedVelocityLengthSquared = combinedVelocity.SqrMagnitude();

            //We are going in the same direction and they aren't avoiding
            if (combinedVelocityLengthSquared > ourVelocityLengthSquared && !closetAgent.GetAbility<Move>().IsAvoidingLeft)
            {
                return Vector2d.zero;
            }

            //Steer to go around it
            ColliderType otherType = closetAgent.Body.Shape;
            if (otherType == ColliderType.Circle)
            {
                Vector2d vectorInOtherDirection = collisionBody.Position - Position;

                //Are we more left or right of them
                bool isLeft = false;
                if (closetAgent.GetAbility<Move>().IsAvoidingLeft)
                {
                    //If they are avoiding, avoid with the same direction as them, so we go the opposite way
                    isLeft = closetAgent.GetAbility<Move>().IsAvoidingLeft;
                }
                else
                {
                    //http://stackoverflow.com/questions/13221873/determining-if-one-2d-vector-is-to-the-right-or-left-of-another
                    long dot = Agent.Body.Velocity.x * -vectorInOtherDirection.y + Agent.Body.Velocity.y * vectorInOtherDirection.x;
                    isLeft = dot > 0;
                }
                IsAvoidingLeft = isLeft;

                //Calculate a right angle of the vector between us
                //http://www.gamedev.net/topic/551175-rotate-vector-90-degrees-to-the-right/#entry4546571
                resultVector = isLeft ? new Vector2d(-vectorInOtherDirection.y, vectorInOtherDirection.x) : new Vector2d(vectorInOtherDirection.y, -vectorInOtherDirection.x);
                resultVector.Normalize();

                //Move it out based on our radius + theirs
                resultVector *= (Agent.Body.Radius + closetAgent.Body.Radius);
            }
            else
            {
                //Not supported
                //otherType == B2Shape.e_polygonShape
                Debug.Log("Collider not supported for avoidance");
            }

            //Steer torwards it, increasing force based on how close we are
            return (resultVector / _minAvoidanceDistance);
        }

        private uint GetNodeHash(GridNode node)
        {
            //TODO: At the moment, the CombinePathVersion is based on the destination... essentially caching the path to the last destination
            //Should this be based on commands instead?
            //Also, a lot of redundancy can be moved into MovementGroupHelper... i.e. getting destination node 
            uint ret = (uint)(node.gridX * GridManager.Width);
            ret += (uint)node.gridY;
            return ret;
        }

        #region Autostopping
        public bool GetCanAutoStop()
        {
            return AutoStopPauser <= 0;
        }

        public bool GetCanCollisionStop()
        {
            return CollisionStopPauser <= 0;
        }

        public void PauseAutoStop()
        {
            AutoStopPauser = AUTO_STOP_PAUSE_TIME;
        }

        public void PauseCollisionStop()
        {
            CollisionStopPauser = AUTO_STOP_PAUSE_TIME;
        }

        //TODO: Improve the naming
        private bool GetLookingForStopPause()
        {
            return StopPauseLooker >= 0;
        }

        /// <summary>
        /// Start the search process for collisions/obstructions that are in the same group.
        /// </summary>
        public void StartLookingForStopPause()
        {
            StopPauseLooker = AUTO_STOP_PAUSE_TIME;
        }
        #endregion

        private Vector2d GetAdjustVector(Vector2d desiredVelocity)
        {
            //The velocity change we want
            var velocityChange = desiredVelocity - cachedBody._velocity;
            var adjustFastMag = velocityChange.FastMagnitude();
            //Cap acceleration vector magnitude
            long accel = decelerating ? timescaledDecceleration : timescaledAcceleration;

            if (adjustFastMag > accel * (accel))
            {
                var mag = FixedMath.Sqrt(adjustFastMag >> FixedMath.SHIFT_AMOUNT);
                //Convert to a force
                velocityChange *= accel.Div(mag);
            }

            return velocityChange;
        }

        protected override void OnExecute(Command com)
        {
            if (com.ContainsData<Vector2d>())
            {
                Agent.StopCast(ID);
                IsCasting = true;
                RegisterGroup();
            }
        }

        public void RegisterGroup(bool moveOnProcessed = true)
        {
            MoveOnGroupProcessed = moveOnProcessed;
            if (MovementGroupHelper.CheckValidAndAlert())
            {
                MovementGroupHelper.LastCreatedGroup.Add(this);
            }
        }

        public void OnGroupProcessed(Vector2d _destination)
        {
            if (MoveOnGroupProcessed)
            {
                StartMove(_destination);
                MoveOnGroupProcessed = false;
            }
            else
            {
                Destination = _destination;
            }

            onGroupProcessed?.Invoke();
        }

        public void StartMove(Vector2d _destination, bool allowUnwalkableEndNode = false)
        {
            _flowFields.Clear();
            straightPath = false;
            _allowUnwalkableEndNode = allowUnwalkableEndNode;

            IsMoving = true;
            StoppedTime = 0;
            Arrived = false;

            Destination = _destination;

            Agent.Animator.SetMovingState(AnimState.Moving);

            //TODO: If next-best-node, autostop more easily
            //Also implement stopping sooner based on distanceToMove
            stuckTime = 0;
            RepathTries = 0;
            IsCasting = true;

            if (!IsGroupMoving)
            {
                DoPathfind = true;
                hasPath = false;
            }
            else
            {
                DoPathfind = false;
                hasPath = true;
            }

            onStartMove?.Invoke();
        }

        public void Arrive()
        {
            StopMove();

            Arrived = true;

            onArrive?.Invoke();
        }

        public void StopMove()
        {
            if (IsMoving)
            {
                RepathTries = 0;

                //TODO: Reset these variables when changing destination/command
                AutoStopPauser = 0;
                CollisionStopPauser = 0;
                StopPauseLooker = 0;
                StopPauseLayer = 0;

                if (MyMovementGroup.IsNotNull())
                {
                    MyMovementGroup.Remove(this);
                }

                IsMoving = false;
                IsAvoidingLeft = false;
                StoppedTime = 0;

                _flowFields.Clear();
                straightPath = false;

                IsCasting = false;

                OnStopMove?.Invoke();
            }
        }

        protected override void OnStopCast()
        {
            StopMove();
        }

        // this helps prevent agents in large groups from trying to get to the middle of the group
        private void HandleCollision(LSBody other)
        {
            if (!CanMove || other.Agent.IsNull())
            {
                return;
            }

            tempAgent = other.Agent;

            Move otherMover = tempAgent.GetAbility<Move>();
            if (otherMover.IsNotNull() && IsMoving)
            {
                //if agent is assigned move group and the other mover is moving to a similar point
                if (MyMovementGroupID >= 0 && otherMover.MyMovementGroupID == MyMovementGroupID
                    || otherMover.Destination.FastDistance(Destination) <= (closingDistance * closingDistance))
                {
                    if (!otherMover.IsMoving)
                    {
                        if (otherMover.Arrived
                            && otherMover.StoppedTime > MinimumOtherStopTime)
                        {
                            Arrive();
                        }
                    }
                }

                if (GetLookingForStopPause())
                {
                    //As soon as the original collision stop unit is released, units will start breaking out of pauses
                    if (!otherMover.GetCanCollisionStop())
                    {
                        StopPauseLayer = -1;
                        PauseAutoStop();
                    }
                    else if (!otherMover.GetCanAutoStop())
                    {
                        if (otherMover.StopPauseLayer < StopPauseLayer)
                        {
                            StopPauseLayer = otherMover.StopPauseLayer + 1;
                            PauseAutoStop();
                        }
                    }
                }
            }
        }

        #region Debug
#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (DrawPath && _flowFieldBuffer.IsNotNull() && !straightPath)
            {
                const float height = 0.25f;
                foreach (KeyValuePair<Vector2d, FlowField> flow in _flowFieldBuffer)
                {
                    FlowField flowField = flow.Value;
                    UnityEditor.Handles.Label(flowField.WorldPos.ToVector3(height), flowField.Distance.ToString());
                    if (flowField.Direction != Vector2d.zero)
                    {
                        Color hasLOS = flowField.HasLOS ? Color.yellow : Color.blue;
                        DrawArrow.ForGizmo(flowField.WorldPos.ToVector3(height), flowField.Direction.ToVector3(height), hasLOS);
                    }
                }
            }
        }
#endif
        #endregion
        protected override void OnSaveDetails(JsonWriter writer)
        {
            base.SaveDetails(writer);
            SaveManager.WriteInt(writer, "MyMovementGroupID", MyMovementGroupID);
            SaveManager.WriteBoolean(writer, "GroupMoving", IsGroupMoving);
            SaveManager.WriteBoolean(writer, "Moving", IsMoving);
            SaveManager.WriteBoolean(writer, "HasPath", hasPath);
            SaveManager.WriteBoolean(writer, "StraightPath", straightPath);
            SaveManager.WriteInt(writer, "StoppedTime", StoppedTime);
            SaveManager.WriteVector2d(writer, "Destination", Destination);
            SaveManager.WriteBoolean(writer, "Arrived", Arrived);
            SaveManager.WriteVector2d(writer, "AveragePosition", AveragePosition);
            SaveManager.WriteBoolean(writer, "Decelerating", decelerating);
            SaveManager.WriteVector2d(writer, "MovementDirection", movementDirection);
        }

        protected override void HandleLoadedProperty(JsonTextReader reader, string propertyName, object readValue)
        {
            base.HandleLoadedProperty(reader, propertyName, readValue);
            switch (propertyName)
            {
                case "MyMovementGroupID":
                    MyMovementGroupID = (int)readValue;
                    break;
                case "GroupMoving":
                    IsGroupMoving = (bool)readValue;
                    break;
                case "Moving":
                    IsMoving = (bool)readValue;
                    break;
                case "HasPath":
                    hasPath = (bool)readValue;
                    break;
                case "StraightPath":
                    straightPath = (bool)readValue;
                    break;
                case "StoppedTime":
                    StoppedTime = (int)readValue;
                    break;
                case "Destination":
                    Destination = LoadManager.LoadVector2d(reader);
                    break;
                case "Arrived":
                    Arrived = (bool)readValue;
                    break;
                case "AveragePosition":
                    AveragePosition = LoadManager.LoadVector2d(reader);
                    break;
                case "Decelerating":
                    decelerating = (bool)readValue;
                    break;
                case "MovementDirection":
                    movementDirection = LoadManager.LoadVector2d(reader);
                    break;
                default: break;
            }
        }
    }
}