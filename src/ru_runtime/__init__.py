"""Vietnamese-English Ru runtime using hosted OpenAI-compatible providers."""

import logging

_package_logger = logging.getLogger(__name__)
if not any(isinstance(handler, logging.NullHandler) for handler in _package_logger.handlers):
    _package_logger.addHandler(logging.NullHandler())
