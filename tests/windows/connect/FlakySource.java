// A source connector for e2e_connect.py: its task fails once when the file <flag.dir>/<name>.fail
// appears (and removes it), like a Debezium task that gives up when its database is gone, and
// runs again once restarted. It produces nothing.
//
// Also e2e.Sleep, a java.exe that only waits, standing in for a JVM left behind by Sorcerer.
package e2e;

import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.List;
import java.util.Map;

import org.apache.kafka.common.config.ConfigDef;
import org.apache.kafka.connect.connector.Task;
import org.apache.kafka.connect.errors.ConnectException;
import org.apache.kafka.connect.source.SourceConnector;
import org.apache.kafka.connect.source.SourceRecord;
import org.apache.kafka.connect.source.SourceTask;

public class FlakySource extends SourceConnector {
    private Map<String, String> props;

    @Override public void start(Map<String, String> props) { this.props = props; }
    @Override public Class<? extends Task> taskClass() { return FlakyTask.class; }
    @Override public List<Map<String, String>> taskConfigs(int maxTasks) { return List.of(props); }
    @Override public void stop() { }
    @Override public ConfigDef config() { return new ConfigDef(); }
    @Override public String version() { return "1"; }

    public static class FlakyTask extends SourceTask {
        private Path flag;

        @Override public String version() { return "1"; }

        @Override public void start(Map<String, String> props) {
            flag = Paths.get(props.get("flag.dir"), props.get("name") + ".fail");
        }

        @Override public List<SourceRecord> poll() throws InterruptedException {
            Thread.sleep(500);
            if (flag.toFile().delete())
                throw new ConnectException("Connect timed out (e2e: " + flag + " was there)");
            return null;
        }

        @Override public void stop() { }
    }
}

class Sleep {
    public static void main(String[] args) throws InterruptedException {
        Thread.sleep(Long.MAX_VALUE);
    }
}
