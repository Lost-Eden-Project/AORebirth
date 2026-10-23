namespace ZoneEngine_New.Core.Ai
{
    using System.Collections.Generic;

    using GroveGames.BehaviourTree.Nodes;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Metrics;

    using Vector3 = AORebirth.Core.Vector.Vector3;

    sealed class ReturnToSpawnNode : BehaviourNode
    {
        readonly NpcBrain _brain;

        public ReturnToSpawnNode(NpcBrain brain)
            : base("return-to-spawn")
        {
            _brain = brain;
        }

        public override NodeState Evaluate(float deltaTime)
        {
            TickStallWatch.Stage("node.return-to-spawn", _brain.Npc.Identity.Instance);
            _brain.StopFighting();
            if (!_brain.HasHome || _brain.HasArrivedHome())
                return _nodeState = NodeState.Success;

            // Run home when there is a route. Snap only when there is none, or the walk home gets
            // stuck; the reset that follows heals either way.
            if (_brain.ReturnHome())
                return _nodeState = NodeState.Running;

            _brain.WarpHome();
            return _nodeState = NodeState.Success;
        }
    }

    sealed class ResetNpcNode : BehaviourNode
    {
        readonly NpcBrain _brain;

        public ResetNpcNode(NpcBrain brain)
            : base("reset")
        {
            _brain = brain;
        }

        public override NodeState Evaluate(float deltaTime)
        {
            TickStallWatch.Stage("node.reset", _brain.Npc.Identity.Instance);
            _brain.ResetOutOfCombat();
            return _nodeState = NodeState.Success;
        }
    }

    sealed class SelectHighestThreatNode : BehaviourNode
    {
        readonly NpcBrain _brain;

        public SelectHighestThreatNode(NpcBrain brain)
            : base("select-threat")
        {
            _brain = brain;
        }

        public override NodeState Evaluate(float deltaTime)
        {
            TickStallWatch.Stage("node.select-threat", _brain.Npc.Identity.Instance);
            return _nodeState = _brain.TrySelectHighestThreat() ? NodeState.Success : NodeState.Failure;
        }
    }

    sealed class MoveTowardTargetNode : BehaviourNode
    {
        readonly NpcBrain _brain;

        public MoveTowardTargetNode(NpcBrain brain)
            : base("move-toward-target")
        {
            _brain = brain;
        }

        public override NodeState Evaluate(float deltaTime)
        {
            TickStallWatch.Stage("node.move-toward-target", _brain.Npc.Identity.Instance);
            // The combat sequence stays on this node while chasing, so re-select here.
            // A new or stolen target has to enter combat on this tick, before arrival.
            _brain.TrySelectHighestThreat();
            Character? target = _brain.ResolveCurrentTarget();
            if (target == null)
                return _nodeState = NodeState.Failure;

            if (_brain.Npc.FightingTarget != target.Identity)
                _brain.Npc.StartFighting(target.Identity, 0);

            // In reach of some weapon: hold there while the NPC is still opening the fight, then close in until the
            // shortest weapon reaches too (NpcBrain.HoldsFightDistance). The weapons keep swinging on the way.
            if (_brain.CanAttackNow(target))
            {
                if (_brain.HoldsFightDistance(target))
                {
                    _brain.StopPathing();
                    return _nodeState = NodeState.Success;
                }

                _brain.PathTo(_brain.ApproachPoint(target));
                return _nodeState = NodeState.Running;
            }

            // Out of every weapon's reach: a plain chase; reaching it again starts with a hold.
            _brain.ResetFightDistance();
            if (_brain.HasUnfinishedPath())
            {
                if (_brain.CanPathTo(target))
                {
                    _brain.PathTo(_brain.ApproachPoint(target));
                    return _nodeState = NodeState.Running;
                }

                // No route from here: the old path is a chance only while it still moves the NPC. One that stalled
                // (stranded off the navmesh, seen live 3 m under a 127 floor with no surface beneath it) used to keep
                // "a chance" for ever and stand there in combat; drop the path and give up below like any other.
                if (!_brain.IsPathStalled())
                    return _nodeState = NodeState.Running;
                _brain.StopPathing();
            }

            if (!_brain.CanPathTo(target))
            {
                if (_brain.HasChanceWithGrace(target))
                    return _nodeState = NodeState.Running;

                _brain.StopFighting();
                return _nodeState = NodeState.Failure;
            }

            _brain.PathTo(_brain.ApproachPoint(target));
            return _nodeState = NodeState.Running;
        }
    }

    sealed class StartFightingNode : BehaviourNode
    {
        readonly NpcBrain _brain;

        public StartFightingNode(NpcBrain brain)
            : base("start-fighting")
        {
            _brain = brain;
        }

        public override NodeState Evaluate(float deltaTime)
        {
            TickStallWatch.Stage("node.start-fighting", _brain.Npc.Identity.Instance);
            Character? target = _brain.ResolveCurrentTarget();
            if (target == null)
                return _nodeState = NodeState.Failure;

            if (_brain.Npc.FightingTarget != target.Identity)
                _brain.Npc.StartFighting(target.Identity, 0);

            return _nodeState = NodeState.Success;
        }
    }

    sealed class PatrolNode : BehaviourNode
    {
        readonly NpcBrain _brain;
        int _index;

        public PatrolNode(NpcBrain brain)
            : base("patrol")
        {
            _brain = brain;
        }

        public override NodeState Evaluate(float deltaTime)
        {
            TickStallWatch.Stage("node.patrol", _brain.Npc.Identity.Instance);
            IReadOnlyList<Vector3> points = _brain.PatrolWaypoints;
            if (points.Count == 0)
            {
                _brain.PatrolAnchor = null;
                return _nodeState = NodeState.Failure;
            }

            if (_index >= points.Count)
                _index = 0;

            // Each NPC walks the route shifted by its own stable offset (NpcSpread), so a shared route does not stack.
            Vector3 dest = NpcSpread.Destination(_brain.Npc, points[_index]);
            if (NpcAiRules.IsNearby(_brain.Npc.Position, dest, NpcAiRules.ArriveHomeMeters))
            {
                // A program bound to this NPC may act at the waypoint (NpcScripts Arrived).
                _brain.Npc.Scripts?.Raise(NpcScripts.NpcScriptTrigger.Arrived, _index);
                _index = (_index + 1) % points.Count;
                dest = NpcSpread.Destination(_brain.Npc, points[_index]);
            }

            // The leash measures from this waypoint while the patrol walks to it (NpcBrain.LeashAnchor).
            _brain.PatrolAnchor = dest;
            _brain.PathTo(dest);
            return _nodeState = NodeState.Running;
        }

        public override void Reset()
        {
            _index = 0;
            _brain.PatrolAnchor = null;
        }
    }

    /// <summary>
    /// A program's walk (NpcScripts): the brain holds a scripted destination and paths there at the requested pace.
    /// Returns Success like WanderNode so the root re-checks leash and hate every think.
    /// </summary>
    sealed class ScriptedMoveNode : BehaviourNode
    {
        readonly NpcBrain _brain;

        public ScriptedMoveNode(NpcBrain brain)
            : base("scripted-move")
        {
            _brain = brain;
        }

        public override NodeState Evaluate(float deltaTime)
        {
            TickStallWatch.Stage("node.script", _brain.Npc.Identity.Instance);
            _brain.TickScriptedMove();
            return _nodeState = NodeState.Success;
        }
    }

    /// <summary>A district NPC's idle activity: pause, walk to a random reachable spot near home, repeat.</summary>
    sealed class WanderNode : BehaviourNode
    {
        readonly NpcBrain _brain;

        public WanderNode(NpcBrain brain)
            : base("wander")
        {
            _brain = brain;
        }

        public override NodeState Evaluate(float deltaTime)
        {
            TickStallWatch.Stage("node.wander", _brain.Npc.Identity.Instance);
            _brain.TickWander();
            // Success, like IdleNode: the root Selector resumes a Running child on the next tick and skips the
            // branches before it, so a Running wander never re-checked leash or hate and a hit NPC kept strolling.
            // The wander's own state lives in the brain, so ending the node each think loses nothing.
            return _nodeState = NodeState.Success;
        }
    }

    sealed class IdleNode : BehaviourNode
    {
        readonly NpcBrain _brain;

        public IdleNode(NpcBrain brain)
            : base("idle")
        {
            _brain = brain;
        }

        public override NodeState Evaluate(float deltaTime)
        {
            TickStallWatch.Stage("node.idle", _brain.Npc.Identity.Instance);
            _brain.StopPathing();
            return _nodeState = NodeState.Success;
        }
    }

    static class NpcAiCombat
    {
        public static void AnnounceStopFight(Character npc)
        {
            if (npc.FightingTarget.Instance == 0)
                return;

            npc.Cell?.Announce(
                new StopFightMessage
                {
                    Identity = npc.Identity,
                    Unknown1 = 1
                });
            npc.SetFightingTarget(Identity.None);
        }
    }
}
