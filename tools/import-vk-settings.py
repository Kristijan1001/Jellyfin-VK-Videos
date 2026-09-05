"""Import literal VK settings without executing the uploader or printing secrets."""
import argparse
import ast
from pathlib import Path
import shutil
from datetime import datetime
import xml.etree.ElementTree as ET

p = argparse.ArgumentParser()
p.add_argument('--source', required=True, type=Path)
p.add_argument('--data-dir', required=True, type=Path)
p.add_argument('--user-id', default='')
args = p.parse_args()
values = {}
for node in ast.parse(args.source.read_text(encoding='utf-8-sig')).body:
    if isinstance(node, ast.Assign):
        for target in node.targets:
            if isinstance(target, ast.Name) and target.id in ('ACCESS_TOKEN', 'CHANNEL_ID'):
                values[target.id] = ast.literal_eval(node.value)
if not isinstance(values.get('ACCESS_TOKEN'), str) or not values['ACCESS_TOKEN'].strip():
    raise SystemExit('No literal ACCESS_TOKEN was found in the source file.')
if not isinstance(values.get('CHANNEL_ID'), int) or values['CHANNEL_ID'] == 0:
    raise SystemExit('No valid literal CHANNEL_ID was found in the source file.')
destination = args.data_dir.resolve() / 'plugins/configurations/Jellyfin.Plugin.VkVideos.xml'
destination.parent.mkdir(parents=True, exist_ok=True)
if destination.exists():
    backup = destination.with_suffix('.xml.backup-' + datetime.now().strftime('%Y%m%d-%H%M%S'))
    shutil.copy2(destination, backup)
root = ET.Element('PluginConfiguration')
for name, value in [('Enabled', 'true'), ('OwnerId', values['CHANNEL_ID']),
                    ('AccessToken', values['ACCESS_TOKEN']), ('RefreshMinutes', 15),
                    ('MaximumHeight', 2160), ('ShowAllVideos', 'false')]:
    ET.SubElement(root, name).text = str(value)
users = ET.SubElement(root, 'AllowedUserIds')
if args.user_id:
    ET.SubElement(users, 'string').text = args.user_id
ET.indent(root)
temp = destination.with_suffix('.xml.tmp')
ET.ElementTree(root).write(temp, encoding='utf-8', xml_declaration=True)
temp.replace(destination)
print('VK settings imported. Credentials were not printed or added to the plugin build.')
