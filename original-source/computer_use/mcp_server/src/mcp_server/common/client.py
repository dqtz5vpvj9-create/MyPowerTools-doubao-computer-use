# Copyright (c) 2025 Bytedance Ltd. and/or its affiliates
# Licensed under the 【火山方舟】原型应用软件自用许可协议
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at 
#     https://www.volcengine.com/docs/82379/1433703
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.

import os
from typing import Optional

from tool_server_client.client import ComputerUseClient, new_computer_use_client

from mcp_server.common.logs import LOG
from mcp_server.common.config import tool_server_config, plugins_config, ssl_config

_local_client = None

# Get client CA certificate path from configuration if HTTPS plugin is enabled.
# It is used to verify the server certificate of tool_server when communicating over HTTPS.
CLIENT_CA: Optional[str] = (
    ssl_config.get("client_ca") if plugins_config.get("enable_https", False) else None
)


def tool_server_client(endpoint: str = None) -> ComputerUseClient:
    global _local_client

    auth_key = os.environ.get("AUTH_API_KEY") or tool_server_config.get("auth_key", "")

    try:
        if tool_server_config.get("local"):
            if _local_client is None:
                endpoint = os.environ.get(
                    "TOOL_SERVER_ENDPOINT") or tool_server_config.get("endpoint")
                _local_client = new_computer_use_client(
                    endpoint, auth_key=auth_key, client_ca=CLIENT_CA
                )

            return _local_client
        else:
            LOG.info(f"Get client, endpoint: {endpoint}")
            return new_computer_use_client(
                endpoint, auth_key=auth_key, client_ca=CLIENT_CA
            )

    except Exception as e:
        LOG.error(f"Get client failed: {str(e)}")
        raise e