from .models import Job


class Scheduler:
    def __init__(self, max_parallel: int) -> None:
        if isinstance(max_parallel, bool) or not isinstance(max_parallel, int) or max_parallel <= 0:
            raise ValueError("max_parallel must be a positive integer")
        self.max_parallel = max_parallel
        self._jobs = {}
        self._states = {}
        self._attempts = {}
        self._history = []

    @property
    def history(self):
        return tuple(self._history)

    def replace(self, jobs):
        try:
            proposed = list(jobs)
        except TypeError as error:
            raise ValueError("jobs must be iterable") from error
        by_id = {}
        for job in proposed:
            if not isinstance(job, Job) or not isinstance(job.id, str) or not job.id.strip():
                raise ValueError("job IDs must be non-empty strings")
            if job.id in by_id:
                raise ValueError("duplicate job ID")
            if isinstance(job.max_attempts, bool) or not isinstance(job.max_attempts, int) or job.max_attempts <= 0:
                raise ValueError("max_attempts must be a positive integer")
            if not isinstance(job.depends_on, tuple) or any(not isinstance(dep, str) for dep in job.depends_on):
                raise ValueError("dependencies must be a tuple of strings")
            if len(set(job.depends_on)) != len(job.depends_on):
                raise ValueError("dependencies must be unique")
            by_id[job.id] = job
        for job in proposed:
            if job.id in job.depends_on or any(dep not in by_id for dep in job.depends_on):
                raise ValueError("invalid dependency")
        visiting, visited = set(), set()

        def visit(job_id):
            if job_id in visiting:
                raise ValueError("dependency cycle")
            if job_id in visited:
                return
            visiting.add(job_id)
            for dep in by_id[job_id].depends_on:
                visit(dep)
            visiting.remove(job_id)
            visited.add(job_id)

        for job_id in by_id:
            visit(job_id)
        self._jobs = by_id
        self._states = dict.fromkeys(by_id, "pending")
        self._attempts = dict.fromkeys(by_id, 0)
        self._history = []

    def state(self, job_id):
        if job_id not in self._states:
            raise ValueError("unknown job")
        return self._states[job_id]

    def ready(self):
        slots = self.max_parallel - sum(state == "running" for state in self._states.values())
        if slots <= 0:
            return ()
        ready = [job_id for job_id, job in self._jobs.items()
                 if self._states[job_id] == "pending"
                 and self._attempts[job_id] < job.max_attempts
                 and all(self._states[dep] == "succeeded" for dep in job.depends_on)]
        return tuple(sorted(ready)[:slots])

    def start(self, job_id):
        if job_id not in self.ready():
            raise ValueError("job is not ready")
        attempt = self._attempts[job_id] + 1
        self._attempts[job_id] = attempt
        self._transition(job_id, "running")
        return attempt

    def succeed(self, job_id):
        self._require_running(job_id)
        self._transition(job_id, "succeeded")
        return "succeeded"

    def fail(self, job_id):
        self._require_running(job_id)
        self._transition(job_id, "failed")
        self._block_descendants(job_id)
        return "failed"

    def retry(self, job_id):
        if job_id not in self._jobs or self._states[job_id] != "failed":
            return False
        if self._attempts[job_id] >= self._jobs[job_id].max_attempts:
            return False
        self._transition(job_id, "pending")
        return True

    def cancel(self, job_id):
        if job_id not in self._jobs or self._states[job_id] not in {"pending", "running"}:
            raise ValueError("job cannot be cancelled")
        self._transition(job_id, "cancelled")
        self._block_descendants(job_id)
        return "cancelled"

    def _require_running(self, job_id):
        if job_id not in self._jobs or self._states[job_id] != "running":
            raise ValueError("job is not running")

    def _transition(self, job_id, to_state):
        from_state = self._states[job_id]
        self._states[job_id] = to_state
        self._history.append((job_id, from_state, to_state, self._attempts[job_id]))

    def _block_descendants(self, failed_id):
        blocked = {failed_id}
        changed = True
        while changed:
            changed = False
            for job_id, job in self._jobs.items():
                if self._states[job_id] == "pending" and any(dep in blocked for dep in job.depends_on):
                    self._transition(job_id, "blocked")
                    blocked.add(job_id)
                    changed = True
