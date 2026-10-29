Complete the dependency planner across its model and planner modules. Return a
stable topological order, preserving input order among ready steps. Reject
unknown dependency IDs, dependency cycles and duplicate step IDs with
ValueError. Do not modify the input records or dependency lists.
