#include <afxwin.h>
#include <atlstr.h>
class Demo : public CWinApp {
public:
    BOOL InitInstance() override {
        CString name(L"Winix MFC ATL");
        auto frame = new CFrameWnd();
        frame->Create(nullptr, name, WS_OVERLAPPEDWINDOW, CRect(100,100,500,300));
        m_pMainWnd = frame; frame->ShowWindow(SW_SHOW); return TRUE;
    }
} app;
