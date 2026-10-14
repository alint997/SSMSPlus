using SSMSPlusHistory.Services;
using System;

namespace SSMSPlusHistory
{
    public class HistoryPlugin
    {
        private bool isRegistred = false;
        private QueryTracker _queryTracker;
        private HistoryUi _historyUi;

        public HistoryPlugin(QueryTracker queryTracker, HistoryUi historyUi)
        {
            _queryTracker = queryTracker;
            _historyUi = historyUi;
        }


        public void Register()
        {
            if (isRegistred)
            {
                throw new Exception("HistoryPlugin is already registred");
            }

            isRegistred = true;
            _queryTracker.StartTraking();
            _historyUi.Register();
        }
    }
}
