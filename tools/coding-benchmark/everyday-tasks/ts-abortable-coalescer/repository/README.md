# Abortable request coalescer

Several UI callers may request the same resource. They share active work, but
canceling one caller must not cancel or strand the others.
