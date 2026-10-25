import os
EXE_PATH = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    r"WinUI3Shell\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\WinUI3Shell.exe",
)
def main() -> None:
    if not os.path.exists(EXE_PATH):
        print(f"未找到可执行文件：{EXE_PATH}")
        return
    os.system(f'start "" "{EXE_PATH}"')
if __name__ == "__main__":
    main()
