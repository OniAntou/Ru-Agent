from src.models import Job
from src.scheduler import Scheduler

scheduler = Scheduler(max_parallel=2)
scheduler.replace([Job("load", (), 1), Job("serve", ("load",), 1)])
assert scheduler.ready() == ("load",)
scheduler.start("load")
scheduler.succeed("load")
assert scheduler.ready() == ("serve",)
print("smoke passed")
