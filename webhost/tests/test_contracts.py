from uuid import uuid4

import pytest
from conftest import implementation
from pydantic import ValidationError


def test_command_byte_limit_and_snapshot_never_accepts_credentials():
    contracts = implementation("contracts")
    with pytest.raises(ValidationError):
        contracts.RunCommand(conversation_id=str(uuid4()), profile_id=str(uuid4()),
                             client_request_id=str(uuid4()), message="Я" * 40000)
    with pytest.raises(ValidationError):
        contracts.ProfileSnapshot(id=str(uuid4()), name="Локальная модель", model="fixture",
                                  base_url="http://127.0.0.1:1234/v1", api_key="canary")


def test_prepared_operation_is_frozen():
    contracts = implementation("contracts")
    operation = contracts.PreparedOperation(operation_id=str(uuid4()), run_id=str(uuid4()),
                                            tool="corel_status", arguments={}, digest="0" * 64)
    with pytest.raises(ValidationError):
        operation.tool = "corel_execute_vba"
