class Scheduler:
    def __init__(self, max_parallel: int) -> None:
        self.max_parallel = max_parallel
        self._jobs = {}
        self._states = {}
        self._attempts = {}
        self._history = []

    @property
    def history(self):
        return tuple(self._history)

    def replace(self, jobs):
        self._jobs = {job.id: job for job in jobs}
        self._states = dict.fromkeys(self._jobs, "pending")
        self._attempts = dict.fromkeys(self._jobs, 0)
        self._history = []

    def state(self, job_id):
        return self._states[job_id]

    def ready(self):
        running = sum(state == "running" for state in self._states.values())
        slots = max(0, self.max_parallel - running)
        return tuple(sorted(job_id for job_id, job in self._jobs.items()
                            if self._states[job_id] == "pending"
                            and all(self._states[dep] == "succeeded" for dep in job.depends_on))[:slots])

    def start(self, job_id):
        if job_id not in self.ready():
            raise ValueError("job is not ready")
        self._attempts[job_id] += 1
        self._states[job_id] = "running"
        return self._attempts[job_id]

    def succeed(self, job_id):
        self._states[job_id] = "succeeded"
        return "succeeded"

    def fail(self, job_id):
        self._states[job_id] = "failed"
        return "failed"

    def retry(self, job_id):
        if self._states[job_id] != "failed" or self._attempts[job_id] >= self._jobs[job_id].max_attempts:
            return False
        self._states[job_id] = "pending"
        return True

    def cancel(self, job_id):
        self._states[job_id] = "cancelled"
        return "cancelled"
