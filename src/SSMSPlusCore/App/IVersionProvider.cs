namespace SSMSPlusCore.App
{
    public interface IVersionProvider
    {
        int GetBuild();
        int[] GetBuildAndRevision();
    }
}
