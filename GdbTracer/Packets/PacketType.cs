namespace GdbTracer.Packets
{
    public enum ApiPacketType
    {
        Allocation = 1,
        Free = 2,
        TestcaseRequest = 3,
        TestcaseStart = 4,
        TestcaseEnd = 5,
        StackInfo = 6,
        StepoverBegin = 7,
        StepoverEnd = 8
    }
}