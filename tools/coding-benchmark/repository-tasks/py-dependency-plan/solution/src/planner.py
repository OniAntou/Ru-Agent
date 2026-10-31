from .steps import Step


def order_steps(steps: list[Step]) -> list[Step]:
    if len({step.id for step in steps}) != len(steps):
        raise ValueError("duplicate step ID")
    by_id = {step.id: step for step in steps}
    for step in steps:
        if any(dependency not in by_id for dependency in step.dependencies):
            raise ValueError("unknown dependency")
    completed: set[str] = set()
    visiting: set[str] = set()
    ordered: list[Step] = []

    def visit(step: Step) -> None:
        if step.id in completed:
            return
        if step.id in visiting:
            raise ValueError("dependency cycle")
        visiting.add(step.id)
        for dependency in step.dependencies:
            visit(by_id[dependency])
        visiting.remove(step.id)
        completed.add(step.id)
        ordered.append(step)

    for step in steps:
        visit(step)
    return ordered
