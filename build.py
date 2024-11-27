import os

# 获取环境变量
commit_sha = os.getenv('CF_PAGES_COMMIT_SHA')

# 如果环境变量存在，则将其写入文件
if commit_sha:
    with open('commit.txt', 'w') as file:
        file.write(commit_sha)
else:
    print("环境变量 'CF_PAGES_COMMIT_SHA' 未设置。")
