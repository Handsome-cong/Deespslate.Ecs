﻿using System.Runtime.InteropServices;

namespace Deepslate.Ecs;

internal sealed class ParallelScheduler
{
    private readonly Stage[] _stages;
    private readonly Archetype[] _archetypes;

    private readonly HashSet<DependencyGraphNode> _runningNodes = [];
    private readonly HashSet<DependencyGraphNode> _waitingNodes = [];

    private readonly List<int> _dependencyCompletionCount = [];
    private readonly List<bool> _executedFlags = [];

    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private readonly UncheckedCommandBuffer _commandBufferEndOfStage = new();
    private readonly UncheckedCommandBuffer _commandBufferEndOfTick = new();

    internal ParallelScheduler(IEnumerable<Stage> stages, IEnumerable<Archetype> archetypes)
    {
        _stages = stages.ToArray();
        _archetypes = archetypes.ToArray();
    }

    public void Tick()
    {
        TickAsync().Wait();
    }

    public async Task TickAsync()
    {
        foreach (var stage in _stages)
        {
            await ExecuteStageAsync(stage);
            _commandBufferEndOfStage.Execute();
        }

        _commandBufferEndOfTick.Execute();
    }

    private async Task ExecuteStageAsync(Stage stage)
    {
        CollectionsMarshal.SetCount(_dependencyCompletionCount, stage.Graph.Nodes.Count);
        CollectionsMarshal.AsSpan(_dependencyCompletionCount).Clear();
        CollectionsMarshal.SetCount(_executedFlags, stage.Graph.Nodes.Count);
        CollectionsMarshal.AsSpan(_executedFlags).Clear();

        foreach (var node in stage.Graph.Nodes)
        {
            _waitingNodes.Add(node);
        }

        await _semaphore.WaitAsync();
        await ExecuteNextNodeAsync();
    }

    private async Task ExecuteNextNodeAsync()
    {
        var tasks = new List<Task>();
        foreach (var node in _waitingNodes)
        {
            var dependencyCount = node.OtherNodesThisDependsOn.Count;
            var completedDependencyCount = _dependencyCompletionCount[node.Id];
            if (completedDependencyCount < dependencyCount)
            {
                continue;
            }

            if (_runningNodes.Any(runningNode => runningNode.OtherNodesConflictWithThis.Contains(node)))
            {
                continue;
            }

            tasks.Add(ExecuteNodeAsync(node));
        }

        _semaphore.Release();
        await Task.WhenAll(tasks);
    }

    private async Task ExecuteNodeAsync(DependencyGraphNode node)
    {
        _runningNodes.Add(node);
        _waitingNodes.Remove(node);

        var tickSystem = node.TickSystem;

        tickSystem.ExecutionTask = new Task(() =>
        {
            tickSystem.Executor.Execute(
                new TickSystemCommand(tickSystem, _commandBufferEndOfStage,_commandBufferEndOfTick));
        });
        tickSystem.ExecutionTask.Start();
        await tickSystem.ExecutionTask;

        await _semaphore.WaitAsync();
        _runningNodes.Remove(node);
        _executedFlags[node.Id] = true;
        foreach (var otherNode in node.OtherNodesDependOnThis)
        {
            _dependencyCompletionCount[otherNode.Id]++;
        }

        AddToWaitingNodesIfNotExecuted(node.OtherNodesDependOnThis);
        AddToWaitingNodesIfNotExecuted(node.OtherNodesConflictWithThis);
        await ExecuteNextNodeAsync();
    }

    private void AddToWaitingNodesIfNotExecuted(IEnumerable<DependencyGraphNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (!_executedFlags[node.Id])
            {
                _waitingNodes.Add(node);
            }
        }
    }
}