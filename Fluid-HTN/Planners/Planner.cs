using System;
using System.Collections.Generic;
using FluidHTN.Compounds;
using FluidHTN.Conditions;
using FluidHTN.PrimitiveTasks;

namespace FluidHTN
{
    /// <summary>
    ///     A planner is a responsible for handling the management of finding plans in a domain, replan when the state of the
    ///     running plan
    ///     demands it, or look for a new potential plan if the world state gets dirty.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class Planner<T> where T : IContext
    {
        // ========================================================= TICK PLAN

        /// <summary>
        ///     Call this with a domain and context instance to have the planner manage plan and task handling for the domain at
        ///     runtime.
        ///     If the plan completes or fails, the planner will find a new plan, or if the context is marked dirty, the planner
        ///     will attempt
        ///     a replan to see whether we can find a better plan now that the state of the world has changed.
        ///     This planner can also be used as a blueprint for writing a custom planner.
        /// </summary>
        /// <param name="domain"></param>
        /// <param name="ctx"></param>
        public void Tick(Domain<T> domain, T ctx, bool allowImmediateReplan = true)
        {
            if (ctx.IsInitialized == false)
                throw new Exception("Context was not initialized!");

            DecompositionStatus decompositionStatus = DecompositionStatus.Failed;
            bool isTryingToReplacePlan = false;
            // Check whether state has changed or the current plan has finished running.
            // and if so, try to find a new plan.
            if (ctx.PlannerContext.CurrentTask == null && (ctx.PlannerContext.Plan.Count == 0) || ctx.IsDirty)
            {
                Queue<PartialPlanEntry> lastPartialPlanQueue = null;

                var worldStateDirtyReplan = ctx.IsDirty;
                ctx.IsDirty = false;

                if (worldStateDirtyReplan)
                {
                    // If we're simply re-evaluating whether to replace the current plan because
                    // some world state got dirt, then we do not intend to continue a partial plan
                    // right now, but rather see whether the world state changed to a degree where
                    // we should pursue a better plan. Thus, if this replan fails to find a better
                    // plan, we have to add back the partial plan temps cached above.
                    if (ctx.HasPausedPartialPlan)
                    {
                        ctx.HasPausedPartialPlan = false;
                        lastPartialPlanQueue = ctx.Factory.CreateQueue<PartialPlanEntry>();
                        while (ctx.PartialPlanQueue.Count > 0)
                        {
                            lastPartialPlanQueue.Enqueue(ctx.PartialPlanQueue.Dequeue());
                        }

                        // We also need to ensure that the last mtr is up to date with the on-going MTR of the partial plan,
                        // so that any new potential plan that is decomposing from the domain root has to beat the currently
                        // running partial plan.
                        ctx.LastMTR.Clear();
                        foreach (var record in ctx.MethodTraversalRecord) ctx.LastMTR.Add(record);

                        if (ctx.DebugMTR)
                        {
                            ctx.LastMTRDebug.Clear();
                            foreach (var record in ctx.MTRDebug) ctx.LastMTRDebug.Add(record);
                        }
                    }
                }

                decompositionStatus = domain.FindPlan(ctx, out var newPlan);
                isTryingToReplacePlan = ctx.PlannerContext.Plan.Count > 0;
                if (decompositionStatus == DecompositionStatus.Succeeded || decompositionStatus == DecompositionStatus.Partial)
                {
                    if (ctx.PlannerContext.OnReplacePlan != null && (ctx.PlannerContext.Plan.Count > 0 || ctx.PlannerContext.CurrentTask != null))
                    {
                        ctx.PlannerContext.OnReplacePlan.Invoke(ctx.PlannerContext.Plan, ctx.PlannerContext.CurrentTask, newPlan);
                    }
                    else if (ctx.PlannerContext.OnNewPlan != null && ctx.PlannerContext.Plan.Count == 0)
                    {
                        ctx.PlannerContext.OnNewPlan.Invoke(newPlan);
                    }

                    ctx.PlannerContext.Plan.Clear();
                    while (newPlan.Count > 0) ctx.PlannerContext.Plan.Enqueue(newPlan.Dequeue());

                    if (ctx.PlannerContext.CurrentTask != null && ctx.PlannerContext.CurrentTask is IPrimitiveTask t)
                    {
                        ctx.PlannerContext.OnStopCurrentTask?.Invoke(t);
                        t.Stop(ctx);
                        ctx.PlannerContext.CurrentTask = null;
                    }

                    // Copy the MTR into our LastMTR to represent the current plan's decomposition record
                    // that must be beat to replace the plan.
                    if (ctx.MethodTraversalRecord != null)
                    {
                        ctx.LastMTR.Clear();
                        foreach (var record in ctx.MethodTraversalRecord) ctx.LastMTR.Add(record);

                        if (ctx.DebugMTR)
                        {
                            ctx.LastMTRDebug.Clear();
                            foreach (var record in ctx.MTRDebug) ctx.LastMTRDebug.Add(record);
                        }
                    }
                }
                else if (lastPartialPlanQueue != null)
                {
                    ctx.HasPausedPartialPlan = true;
                    ctx.PartialPlanQueue.Clear();
                    while (lastPartialPlanQueue.Count > 0)
                    {
                        ctx.PartialPlanQueue.Enqueue(lastPartialPlanQueue.Dequeue());
                    }
                    ctx.Factory.FreeQueue(ref lastPartialPlanQueue);

                    if (ctx.LastMTR.Count > 0)
                    {
                        ctx.MethodTraversalRecord.Clear();
                        foreach (var record in ctx.LastMTR) ctx.MethodTraversalRecord.Add(record);
                        ctx.LastMTR.Clear();

                        if (ctx.DebugMTR)
                        {
                            ctx.MTRDebug.Clear();
                            foreach (var record in ctx.LastMTRDebug) ctx.MTRDebug.Add(record);
                            ctx.LastMTRDebug.Clear();
                        }
                    }
                }
            }

            if (ctx.PlannerContext.CurrentTask == null && ctx.PlannerContext.Plan.Count > 0)
            {
                ctx.PlannerContext.CurrentTask = ctx.PlannerContext.Plan.Dequeue();
                if (ctx.PlannerContext.CurrentTask != null)
                {
                    ctx.PlannerContext.OnNewTask?.Invoke(ctx.PlannerContext.CurrentTask);
                    foreach (var condition in ctx.PlannerContext.CurrentTask.Conditions)
                        // If a condition failed, then the plan failed to progress! A replan is required.
                        if (condition.IsValid(ctx) == false)
                        {
                            ctx.PlannerContext.OnNewTaskConditionFailed?.Invoke(ctx.PlannerContext.CurrentTask, condition);

                            ctx.PlannerContext.CurrentTask = null;
                            ctx.PlannerContext.Plan.Clear();

                            ctx.LastMTR.Clear();
                            if (ctx.DebugMTR) ctx.LastMTRDebug.Clear();

                            ctx.HasPausedPartialPlan = false;
                            ctx.PartialPlanQueue.Clear();
                            ctx.IsDirty = false;

                            return;
                        }
                }
            }

            if (ctx.PlannerContext.CurrentTask != null)
                if (ctx.PlannerContext.CurrentTask is IPrimitiveTask task)
                {
                    if (task.Operator != null)
                    {
                        foreach (var condition in task.ExecutingConditions)
                            // If a condition failed, then the plan failed to progress! A replan is required.
                            if (condition.IsValid(ctx) == false)
                            {
                                ctx.PlannerContext.OnCurrentTaskExecutingConditionFailed?.Invoke(task, condition);

                                ctx.PlannerContext.CurrentTask = null;
                                ctx.PlannerContext.Plan.Clear();

                                ctx.LastMTR.Clear();
                                if (ctx.DebugMTR) ctx.LastMTRDebug.Clear();

                                ctx.HasPausedPartialPlan = false;
                                ctx.PartialPlanQueue.Clear();
                                ctx.IsDirty = false;

                                return;
                            }

                        ctx.PlannerContext.LastStatus = task.Operator.Update(ctx);

                        // If the operation finished successfully, we set task to null so that we dequeue the next task in the plan the following tick.
                        if (ctx.PlannerContext.LastStatus == TaskStatus.Success)
                        {
                            ctx.PlannerContext.OnCurrentTaskCompletedSuccessfully?.Invoke(task);

                            // All effects that is a result of running this task should be applied when the task is a success.
                            foreach (var effect in task.Effects)
                            {
                                if (effect.Type == EffectType.PlanAndExecute)
                                {
                                    ctx.PlannerContext.OnApplyEffect?.Invoke(effect);
                                    effect.Apply(ctx);
                                }
                            }

                            ctx.PlannerContext.CurrentTask = null;
                            if (ctx.PlannerContext.Plan.Count == 0)
                            {
                                ctx.LastMTR.Clear();
                                if (ctx.DebugMTR) ctx.LastMTRDebug.Clear();

                                ctx.IsDirty = false;

                                if (allowImmediateReplan) Tick(domain, ctx, allowImmediateReplan: false);
                            }
                        }

                        // If the operation failed to finish, we need to fail the entire plan, so that we will replan the next tick.
                        else if (ctx.PlannerContext.LastStatus == TaskStatus.Failure)
                        {
                            ctx.PlannerContext.OnCurrentTaskFailed?.Invoke(task);

                            ctx.PlannerContext.CurrentTask = null;
                            ctx.PlannerContext.Plan.Clear();

                            ctx.LastMTR.Clear();
                            if (ctx.DebugMTR) ctx.LastMTRDebug.Clear();

                            ctx.HasPausedPartialPlan = false;
                            ctx.PartialPlanQueue.Clear();
                            ctx.IsDirty = false;
                        }

                        // Otherwise the operation isn't done yet and need to continue.
                        else
                        {
                            ctx.PlannerContext.OnCurrentTaskContinues?.Invoke(task);
                        }
                    }
                    else
                    {
                        // This should not really happen if a domain is set up properly.
                        ctx.PlannerContext.CurrentTask = null;
                        ctx.PlannerContext.LastStatus = TaskStatus.Failure;
                    }
                }

            if (ctx.PlannerContext.CurrentTask == null && ctx.PlannerContext.Plan.Count == 0 && isTryingToReplacePlan == false &&
                (decompositionStatus == DecompositionStatus.Failed ||
                 decompositionStatus == DecompositionStatus.Rejected))
            {
                ctx.PlannerContext.LastStatus = TaskStatus.Failure;
            }
        }

        // ========================================================= RESET

        public void Reset(IContext ctx)
        {
            ctx.PlannerContext.Plan.Clear();

            if (ctx.PlannerContext.CurrentTask != null && ctx.PlannerContext.CurrentTask is IPrimitiveTask task)
            {
                task.Stop(ctx);
            }
            ctx.PlannerContext.CurrentTask = null;
        }
    }
}
