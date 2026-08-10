namespace Nanto;

public enum NantoFailureStage
{
    Startup,
    Runtime,
    Teardown,
}

public enum RendererFailureKind
{
    Exited,
    Unresponsive,
    FrameRendererExited,
    Unknown,
}