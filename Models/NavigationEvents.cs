namespace SLSKDONET.Models
{
    public class NavigateToPageEvent
    {
        public string PageName { get; }

        public NavigateToPageEvent(string pageName)
        {
            PageName = pageName;
        }
    }
}
