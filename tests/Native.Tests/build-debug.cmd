@echo off
call "D:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul || exit /b 1
"D:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" -S native/pogget-bridge -B artifacts/native-debug -G Ninja -DCMAKE_BUILD_TYPE=Debug -DCMAKE_MAKE_PROGRAM="D:/Program Files/Microsoft Visual Studio/18/Community/Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe" || exit /b 1
"D:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" --build artifacts/native-debug --parallel 1 || exit /b 1
"D:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\ctest.exe" --test-dir artifacts/native-debug --output-on-failure
