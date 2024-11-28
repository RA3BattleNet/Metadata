import os
import xml.etree.ElementTree as ET

def update_commit_sha(element, commit_sha):
    """递归地更新元素及其子元素中的Commit属性值"""
    if 'Commit' in element.attrib and '{ENV:CF_PAGES_COMMIT_SHA}' in element.get('Commit'):
        element.set('Commit', commit_sha)
    for child in element:
        update_commit_sha(child, commit_sha)

def process_xml_file(file_path, commit_sha):
    """处理单个XML文件，替换指定的Commit属性值，并保存修改后的文件"""
    tree = ET.parse(file_path)
    root = tree.getroot()
    
    # 更新当前文件中的Commit属性
    update_commit_sha(root, commit_sha)
    
    # 处理Includes标签下的Module元素
    includes = root.find('Includes')
    if includes is not None:
        for module in includes.findall('Module'):
            source = module.get('Source')
            if source:
                # 构建绝对路径（假设所有相对路径基于metadata.xml所在目录）
                included_file_path = os.path.join(os.path.dirname(file_path), source)
                process_xml_file(included_file_path, commit_sha)  # 递归处理被引用的XML文件
    
    # 将更改写回文件
    tree.write(file_path, encoding='utf-8', xml_declaration=True)

# 主函数
def main():
    # 获取环境变量CF_PAGES_COMMIT_SHA的值
    commit_sha = os.getenv('CF_PAGES_COMMIT_SHA', 'null')  # 如果未设置，则使用默认值

    # 指定XML文件路径
    initial_file_path = 'metadata.xml'

    # 开始处理
    process_xml_file(initial_file_path, commit_sha)

if __name__ == '__main__':
    main()