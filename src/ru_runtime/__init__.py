"""Vietnamese-English Ru runtime using hosted OpenAI-compatible providers."""

import logging

_root_logger = logging.getLogger()
if not _root_logger.handlers:
    logging.basicConfig(
        level=logging.INFO,
        format="%(message)s",
    )
else:
    # Do not replace handlers installed by the host application. The worker
    # adds its privacy-safe diagnostic handler alongside the console handler.
    _root_logger.setLevel(logging.INFO)
